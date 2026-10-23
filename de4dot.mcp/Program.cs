using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using de4dot.code;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace de4dot.mcp {
    // Hardened server.
    //
    // * Every job runs de4dot in a separate child process (this binary with --run-cli), with a
    //   clean environment, a wall-clock timeout and capped log output. A hung or crashing parse
    //   can't take the server down or leak state into the next job. Although de4dotEx disables
    //   execution of code from input assemblies by default, process isolation is not a security
    //   sandbox. Run the server as an unprivileged user (as the Docker image does)
    //   and use OS-level restrictions where practical.
    // * Client-supplied CLI options go through an allowlist with the exact arity of the real
    //   de4dot command line, so no client token can turn into an extra input file. Nothing that
    //   names a path, loads code, or compiles a client regex is accepted. Deobfuscator types and
    //   their options are read from de4dot.cui at startup ("--list-options" prints the table).
    // * HTTP mode listens on 127.0.0.1 unless DE4DOT_HTTP_BIND says otherwise, and then requires
    //   a bearer token (DE4DOT_API_TOKEN). Browser requests (Origin / Sec-Fetch-Site) are refused,
    //   and without a token only a loopback Host header is accepted. Uploads are size limited,
    //   only read once a job slot is free, stored under a private per-job directory with a fixed
    //   name, and deleted afterwards. The client's file name is never used as a path.
    // * Stdio MCP mode only reads inputs and writes outputs inside DE4DOT_MCP_ROOT (default: the
    //   current directory) and never overwrites an existing file. de4dot works on a private copy
    //   and the result is created with O_EXCL semantics after the destination is re-checked.
    class Program {
        static int MaxConcurrentJobs => EnvInt("DE4DOT_MAX_JOBS", 2, 1, 64);
        static int JobTimeoutSeconds => EnvInt("DE4DOT_TIMEOUT_SECONDS", 120, 5, 3600);
        static long MaxUploadBytes => EnvInt("DE4DOT_MAX_UPLOAD_MB", 100, 1, 2048) * 1024L * 1024;
        const int MaxLogChars = 256 * 1024;
        // How long an HTTP request waits for a free job slot before it gets 503. The upload is
        // not read before a slot is held, so waiting requests don't buffer anything.
        static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);

        static async Task<int> Main(string[] args) {
            if (args.Length > 0 && args[0] == "--run-cli") {
                // Child process: the actual de4dot run.
                var cliArgs = new string[args.Length - 1];
                Array.Copy(args, 1, cliArgs, 0, cliArgs.Length);
                return de4dot.cui.Program.Main(cliArgs);
            }

            if (args.Length > 0 && args[0] == "--list-options") {
                PrintOptionTable();
                return 0;
            }

            if (args.Length > 0 && (args[0] == "--http" || args[0] == "-h")) {
                int port = 8080;
                if (args.Length > 1 && int.TryParse(args[1], out int parsedPort) && parsedPort > 0 && parsedPort < 65536)
                    port = parsedPort;
                return await StartHttpServer(port);
            }

            await StartMcpStdioLoop();
            return 0;
        }

        static int EnvInt(string name, int defaultValue, int min, int max) {
            var s = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(s) || !int.TryParse(s, out int v))
                return defaultValue;
            return Math.Clamp(v, min, max);
        }

        #region Running de4dot

        sealed class JobResult {
            public int ExitCode;
            public bool TimedOut;
            public string Log = "";
        }

        static readonly SemaphoreSlim jobSlots = new SemaphoreSlim(MaxConcurrentJobs, MaxConcurrentJobs);

        // The caller must hold a jobSlots slot.
        static async Task<JobResult> RunDe4dotAsync(IReadOnlyList<string> cliArgs, string workDir) {
            var psi = new ProcessStartInfo {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };
            // Re-run ourselves. Under "dotnet de4dot.mcp.dll" the process is dotnet itself.
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
            psi.FileName = self;
            if (string.Equals(Path.GetFileNameWithoutExtension(self), "dotnet", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(typeof(Program).Assembly.Location);
            psi.ArgumentList.Add("--run-cli");
            foreach (var a in cliArgs)
                psi.ArgumentList.Add(a);

            // Clean environment: nothing inherited can change runtime or de4dot behaviour.
            psi.Environment.Clear();
            foreach (var keep in new[] { "PATH", "HOME", "TMPDIR", "DOTNET_ROOT", "LANG", "LC_ALL" }) {
                var v = Environment.GetEnvironmentVariable(keep);
                if (v != null)
                    psi.Environment[keep] = v;
            }
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

            using var process = new Process { StartInfo = psi };
            var log = new StringBuilder();
            bool truncated = false;
            void Append(string? line) {
                if (line == null)
                    return;
                lock (log) {
                    if (truncated)
                        return;
                    int room = MaxLogChars - log.Length;
                    if (line.Length + Environment.NewLine.Length <= room) {
                        log.AppendLine(line);
                        return;
                    }
                    if (room > 0)
                        log.Append(line, 0, Math.Min(line.Length, room));
                    truncated = true;
                }
            }
            process.OutputDataReceived += (s, e) => Append(e.Data);
            process.ErrorDataReceived += (s, e) => Append(e.Data);
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(JobTimeoutSeconds));
            var result = new JobResult();
            string? note = null;
            try {
                await process.WaitForExitAsync(cts.Token);
                result.ExitCode = process.ExitCode;
            }
            catch (OperationCanceledException) {
                result.TimedOut = true;
                result.ExitCode = -1;
                note = $"de4dot was stopped after {JobTimeoutSeconds} s (DE4DOT_TIMEOUT_SECONDS).";
                try {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"[de4dotEx] Could not kill de4dot child process {process.Id}: {ex.Message}");
                }
                // Make sure nothing is still writing into the job directory before the caller deletes it.
                try {
                    if (!process.WaitForExit(30_000))
                        Console.Error.WriteLine($"[de4dotEx] de4dot child process {process.Id} did not exit after kill.");
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"[de4dotEx] Waiting for de4dot child process failed: {ex.Message}");
                }
            }
            lock (log) {
                if (truncated)
                    log.AppendLine().AppendLine($"[log truncated at {MaxLogChars} characters]");
                if (note != null)
                    log.AppendLine(note);
                result.Log = log.ToString();
            }
            return result;
        }

        // Client option table. Every entry has the same arity as the real CLI option, so a value is
        // always consumed together with its option and no client token can end up as a bare argument
        // (which the CLI would treat as another input file, "-f").
        //
        // Global options must come before the input file (the CLI copies them into the file's options
        // when it sees the file), file options after it.
        enum OptionScope { Global, File }

        sealed class OptionSpec {
            public readonly OptionScope Scope;
            public readonly string Kind; // shown by --list-options
            public readonly Regex? Value; // null: the option takes no value
            public OptionSpec(OptionScope scope, string kind, Regex? value) {
                Scope = scope;
                Kind = kind;
                Value = value;
            }
        }

        // Value checks per option class. They come on top of the checks in ParseOptions (no leading
        // '-', no '/', '\', ':' or '~'). Strings are plain words: a string option could name a file.
        static readonly Regex boolValue = new Regex("^(true|false|on|off|0|1)$");
        static readonly Regex intValue = new Regex("^[0-9]{1,9}$");
        static readonly Regex stringValue = new Regex("^[A-Za-z0-9_,+=]{1,64}$");

        // Real CLI options a client may not use, with the reason. Everything else that isn't in
        // clientOptions is rejected as unknown.
        static readonly Dictionary<string, string> deniedOptions = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "--load-new-process", "loads the input into a new process" },
            { "--one-file", "not supported by the server" },
            { "--strtok", "string decrypter methods are not accepted" },
            { "--default-strtok", "string decrypter methods are not accepted" },
            { "--default-strtyp", "use --strtyp" },
            { "--unsafe-code-execution", "not allowed in server mode" },
            { "-d", "not supported by the server" },
            { "-h", "not supported by the server" },
            { "--help", "not supported by the server" },
        };

        // Options only the server itself puts on the child command line (all take a value).
        static readonly HashSet<string> serverValueOptions = new HashSet<string>(StringComparer.Ordinal) { "-o", "--asm-path" };

        sealed class OptionTable {
            public readonly Dictionary<string, OptionSpec> Allowed = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);
            // Deobfuscator options the server found but refuses, with the reason.
            public readonly Dictionary<string, string> Refused = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        static readonly OptionTable optionTable = BuildOptionTable();
        static Dictionary<string, OptionSpec> clientOptions => optionTable.Allowed;

        static OptionTable BuildOptionTable() {
            var table = new OptionTable();
            var d = table.Allowed;

            // Global and file options. These are not read from de4dot.cui: CommandLineParser keeps
            // them as private NoArgOption/OneArgOption closures, and what decides whether one is safe
            // (does the value name a path, does it load code, must it come before or after the file)
            // only exists in the closure body. So the server allows this short explicit list and
            // refuses every other global or file option.
            foreach (var name in new[] { "-v", "-vv", "--dont-rename", "--dont-create-params", "--dont-restore-props", "--no-cflow-deob", "--only-cflow-deob" })
                d.Add(name, new OptionSpec(OptionScope.Global, "none", null));
            d.Add("--keep-names", new OptionSpec(OptionScope.Global, "flags", new Regex("^[ntpefmagd]{1,9}$")));
            d.Add("--preserve-table", new OptionSpec(OptionScope.Global, "flags", new Regex("^[a-z]{1,3}(,-?[a-z]{1,3}){0,15}$")));
            foreach (var name in new[] { "--keep-types", "--preserve-tokens", "--preserve-all", "--preserve-strings", "--preserve-us", "--preserve-blob", "--preserve-sig-data" })
                d.Add(name, new OptionSpec(OptionScope.File, "none", null));
            // delegate and emulate run the input's string decrypter; never accepted from a client.
            d.Add("--strtyp", new OptionSpec(OptionScope.File, "type", new Regex("^(none|default|static)$")));

            // Deobfuscators and their options come from de4dot.cui itself (the same list the CLI
            // registers), so new deobfuscators and options need no change here. The value check is
            // picked from the option class; regex options and option classes without a known value
            // syntax are refused.
            var types = new List<string>();
            foreach (var info in de4dot.cui.Program.CreateDeobfuscatorInfos()) {
                var type = info.Type;
                if (type == null || !Regex.IsMatch(type, "^[A-Za-z0-9]{1,32}$"))
                    continue;
                types.Add(Regex.Escape(type));
                foreach (var option in info.GetOptions()) {
                    var name = option.LongName ?? option.ShortName;
                    if (name == null)
                        continue;
                    string? reason = null;
                    string kind = "";
                    Regex? value = null;
                    var prefix = "--" + type + "-";
                    if (option.ShortName != null || !name.StartsWith(prefix, StringComparison.Ordinal))
                        reason = "not a --<type>-<name> option";
                    else if (d.ContainsKey(name) || deniedOptions.ContainsKey(name) || serverValueOptions.Contains(name))
                        reason = "clashes with another option";
                    else if (Regex.IsMatch(name.Substring(prefix.Length), "path|file|dir", RegexOptions.IgnoreCase))
                        reason = "may name a file or directory";
                    else if (option is NameRegexOption || option is RegexOption)
                        // de4dot compiles these without a match timeout (ReDoS).
                        reason = "regex options are not accepted";
                    // Exact classes only: a subclass may parse its value differently.
                    else if (option.GetType() == typeof(BoolOption))
                        (kind, value) = ("bool", boolValue);
                    else if (option.GetType() == typeof(IntOption))
                        (kind, value) = ("int", intValue);
                    else if (option.GetType() == typeof(StringOption))
                        (kind, value) = ("string", stringValue);
                    else
                        reason = $"{option.GetType().Name} options are not supported by the server";
                    if (reason != null || !d.TryAdd(name, new OptionSpec(OptionScope.Global, kind, value)))
                        table.Refused.TryAdd(name, reason ?? "clashes with another option");
                }
            }
            d.Add("-p", new OptionSpec(OptionScope.File, "type", new Regex("^(" + string.Join("|", types) + ")$")));
            return table;
        }

        // Prints the option table (--list-options), so operators can see what clients may use.
        static void PrintOptionTable() {
            var names = new List<string>(clientOptions.Keys);
            names.Sort(StringComparer.Ordinal);
            Console.WriteLine("Accepted client options (option, scope, value):");
            foreach (var name in names) {
                var spec = clientOptions[name];
                var value = spec.Kind == "flags" || spec.Kind == "type" ? $"{spec.Kind} {spec.Value}" : spec.Kind;
                Console.WriteLine($"  {name} {spec.Scope} {value}");
            }
            var refused = new List<string>(optionTable.Refused.Keys);
            refused.Sort(StringComparer.Ordinal);
            Console.WriteLine("Refused deobfuscator options:");
            foreach (var name in refused)
                Console.WriteLine($"  {name} ({optionTable.Refused[name]})");
        }

        sealed class ParsedOptions {
            public readonly List<string> Global = new List<string>();
            public readonly List<string> File = new List<string>();
        }

        static ParsedOptions ParseOptions(string? options, out string? error) {
            error = null;
            var result = new ParsedOptions();
            if (string.IsNullOrWhiteSpace(options))
                return result;
            if (options.Length > 2048) {
                error = "options too long";
                return result;
            }
            var tokens = options.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++) {
                var t = tokens[i];
                if (clientOptions.TryGetValue(t, out var spec)) {
                    var list = spec.Scope == OptionScope.Global ? result.Global : result.File;
                    list.Add(t);
                    if (spec.Value == null)
                        continue;
                    // The CLI always takes the next token as the value, so do the same.
                    if (i + 1 >= tokens.Length) {
                        error = $"missing value for {t}";
                        return result;
                    }
                    var v = tokens[++i];
                    if (v.Length > 256 || v.StartsWith("-", StringComparison.Ordinal) || v.IndexOfAny(new[] { '/', '\\', ':', '~' }) >= 0 || !spec.Value.IsMatch(v)) {
                        error = $"bad value for {t}";
                        return result;
                    }
                    list.Add(v);
                    continue;
                }
                if (deniedOptions.TryGetValue(t, out var reason) || optionTable.Refused.TryGetValue(t, out reason)) {
                    error = $"option not allowed: {t} ({reason})";
                    return result;
                }
                error = $"option not allowed: {t}";
                return result;
            }
            return result;
        }

        // Child command line: <global options> [--asm-path dir] input -o output <file options>.
        static List<string> BuildCliArgs(ParsedOptions options, string input, string output, string? asmPath) {
            var args = new List<string>(options.Global);
            if (asmPath != null) {
                args.Add("--asm-path");
                args.Add(asmPath);
            }
            args.Add(input);
            args.Add("-o");
            args.Add(output);
            args.AddRange(options.File);
            // Defence in depth: parse it the way de4dot.cui's CommandLineParser does and make sure
            // the only bare argument (an input file) is ours.
            int bare = 0;
            for (int i = 0; i < args.Count; i++) {
                var a = args[i];
                if (clientOptions.TryGetValue(a, out var spec)) {
                    if (spec.Value != null)
                        i++;
                }
                else if (serverValueOptions.Contains(a))
                    i++;
                else if (a == input)
                    bare++;
                else
                    throw new InvalidOperationException("Unexpected token on de4dot command line");
            }
            if (bare != 1)
                throw new InvalidOperationException("Unexpected input count on de4dot command line");
            return args;
        }

        #endregion

        #region HTTP Web API Mode

        static async Task<int> StartHttpServer(int port) {
            var bind = Environment.GetEnvironmentVariable("DE4DOT_HTTP_BIND");
            var address = string.IsNullOrEmpty(bind) ? IPAddress.Loopback : IPAddress.Parse(bind);
            var token = Environment.GetEnvironmentVariable("DE4DOT_API_TOKEN");
            if (!IPAddress.IsLoopback(address) && (token == null || token.Length < 32)) {
                Console.Error.WriteLine("[de4dotEx] Refusing to listen on a non-loopback address without DE4DOT_API_TOKEN (at least 32 characters).");
                return 2;
            }
            var tokenBytes = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => {
                options.Listen(address, port);
                options.Limits.MaxRequestBodySize = MaxUploadBytes + 64 * 1024;
                options.AddServerHeader = false;
            });
            builder.Services.Configure<FormOptions>(o => {
                o.MultipartBodyLengthLimit = MaxUploadBytes;
                o.ValueCountLimit = 16;
            });

            var app = builder.Build();

            app.Use(async (context, next) => {
                // Browsers: a page on any site can POST a multipart form to 127.0.0.1. Browsers
                // send Origin on such requests (and Sec-Fetch-Site); API clients like curl don't.
                var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
                if (context.Request.Headers.ContainsKey("Origin") ||
                    string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fetchSite, "same-site", StringComparison.OrdinalIgnoreCase)) {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (tokenBytes == null) {
                    // No token: only loopback Host names, so a DNS rebinding page can't talk to us.
                    if (!IsLoopbackHost(context.Request.Host.Host)) {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }
                else {
                    // Bearer token check for every request when a token is configured.
                    var header = context.Request.Headers.Authorization.ToString();
                    var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(header.Substring(7)) : Array.Empty<byte>();
                    if (!CryptographicOperations.FixedTimeEquals(given, tokenBytes)) {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }
                await next();
            });

            app.MapGet("/", () => "de4dotEx (hardened) Web API. POST /deobfuscate with multipart field 'file' (and optional 'options').\n");

            app.MapPost("/deobfuscate", async (HttpContext context) => {
                if (!context.Request.HasFormContentType)
                    return Results.BadRequest("Request must be multipart/form-data with a 'file' field.");

                // Take a job slot before reading the body, so busy servers don't buffer uploads
                // for an unbounded queue of waiting requests.
                if (!await jobSlots.WaitAsync(SlotWait, context.RequestAborted)) {
                    context.Response.Headers.RetryAfter = "10";
                    return Results.Problem("Server busy, try again later.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                try {
                    IFormCollection form;
                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)) {
                        readCts.CancelAfter(TimeSpan.FromSeconds(JobTimeoutSeconds));
                        try {
                            form = await context.Request.ReadFormAsync(readCts.Token);
                        }
                        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) {
                            return Results.Problem("Upload took too long.", statusCode: StatusCodes.Status408RequestTimeout);
                        }
                    }
                    var file = form.Files.GetFile("file");
                    if (file == null || file.Length == 0)
                        return Results.BadRequest("No file uploaded under form key 'file'.");
                    if (file.Length > MaxUploadBytes)
                        return Results.BadRequest("File too large.");

                    var options = ParseOptions(form["options"].ToString(), out var optionsError);
                    if (optionsError != null)
                        return Results.BadRequest(optionsError);

                    // Fixed names in a private directory; the uploaded name is never used as a path.
                    var jobDir = CreateJobDir();
                    try {
                        var ext = SafeExtension(file.FileName);
                        var input = Path.Combine(jobDir, "input" + ext);
                        var output = Path.Combine(jobDir, "output" + ext);
                        using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write))
                            await file.CopyToAsync(stream);

                        var result = await RunDe4dotAsync(BuildCliArgs(options, input, output, null), jobDir);

                        if (result.TimedOut)
                            return Results.Problem("Deobfuscation timed out.", statusCode: StatusCodes.Status504GatewayTimeout);
                        if (result.ExitCode != 0 || !File.Exists(output))
                            return Results.Problem("Deobfuscation failed inside the de4dotEx analysis engine.");

                        var bytes = await File.ReadAllBytesAsync(output);
                        return Results.File(bytes, "application/octet-stream", "cleaned_" + SafeDownloadName(file.FileName));
                    }
                    finally {
                        DeleteJobDir(jobDir);
                    }
                }
                finally {
                    jobSlots.Release();
                }
            });

            Console.WriteLine($"[de4dotEx] HTTP API listening on {address}:{port}{(tokenBytes != null ? " (token required)" : "")}");
            await app.RunAsync();
            return 0;
        }

        static bool IsLoopbackHost(string host) =>
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]";

        static string CreateJobDir() {
            var jobDir = Directory.CreateTempSubdirectory("de4dot-job-").FullName;
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(jobDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return jobDir;
        }

        static void DeleteJobDir(string jobDir) {
            try {
                Directory.Delete(jobDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                Console.Error.WriteLine($"[de4dotEx] Could not delete {jobDir}: {ex.Message}");
            }
        }

        static string SafeExtension(string? fileName) {
            var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext == ".exe" || ext == ".dll" || ext == ".netmodule" ? ext : ".dll";
        }

        static string SafeDownloadName(string? fileName) {
            var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
            var sb = new StringBuilder();
            foreach (var c in name)
                sb.Append(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            var s = sb.ToString().Trim('.');
            return s.Length == 0 ? "assembly.dll" : s.Length > 100 ? s.Substring(0, 100) : s;
        }

        #endregion

        #region Stdio MCP Mode

        static async Task StartMcpStdioLoop() {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;

            var reader = Console.In;
            var writer = Console.Out;

            while (true) {
                var line = await reader.ReadLineAsync();
                if (line == null)
                    break;

                // The request id is echoed back unchanged (a number stays a number).
                JsonNode? id = null;
                try {
                    JsonNode? jsonNode;
                    try {
                        jsonNode = JsonNode.Parse(line);
                    }
                    catch (JsonException) {
                        SendError(writer, null, -32700, "Parse error");
                        continue;
                    }
                    if (jsonNode is not JsonObject request) {
                        SendError(writer, null, -32600, "Invalid Request");
                        continue;
                    }

                    var method = request["method"]?.ToString();
                    id = request["id"]?.DeepClone();

                    if (method == "initialize") {
                        SendResponse(writer, id, new {
                            protocolVersion = "2024-11-05",
                            capabilities = new { tools = new { } },
                            serverInfo = new { name = "de4dotEx", version = "1.1.0" }
                        });
                    }
                    else if (method == "notifications/initialized") {
                        // Client notification. No response required.
                    }
                    else if (method == "tools/list") {
                        SendResponse(writer, id, new {
                            tools = new[] {
                                new {
                                    name = "deobfuscate",
                                    description = "Deobfuscate and unpack a .NET assembly (EXE or DLL) using de4dotEx. de4dot runs in a separate child process, but that process is not a sandbox: some deobfuscators interpret or run code from the input, so only use this on a host or container where that is acceptable. Paths must be inside the server's DE4DOT_MCP_ROOT.",
                                    inputSchema = new {
                                        type = "object",
                                        properties = new {
                                            file_path = new {
                                                type = "string",
                                                description = "Path of the .NET assembly to deobfuscate (inside DE4DOT_MCP_ROOT)."
                                            },
                                            output_path = new {
                                                type = "string",
                                                description = "Optional output path inside DE4DOT_MCP_ROOT. Must not exist yet. Default: <input>-cleaned<ext>."
                                            },
                                            options = new {
                                                type = "string",
                                                description = "Optional de4dot options, e.g. '-p crx', '--strtyp none', '--dont-rename', '--keep-types', '--df-inline false'. Per-deobfuscator options (--<type>-<name>) take an explicit value. Options that name files, load code, or take a regex are not accepted."
                                            }
                                        },
                                        required = new[] { "file_path" }
                                    }
                                }
                            }
                        });
                    }
                    else if (method == "tools/call") {
                        var toolName = request["params"]?["name"]?.ToString();
                        if (toolName == "deobfuscate") {
                            var argsObj = request["params"]?["arguments"];
                            var result = await ExecuteDeobfuscation(
                                argsObj?["file_path"]?.ToString(),
                                argsObj?["output_path"]?.ToString(),
                                argsObj?["options"]?.ToString());
                            SendResponse(writer, id, result);
                        }
                        else {
                            SendError(writer, id, -32601, "Unknown tool.");
                        }
                    }
                    else if (id != null) {
                        SendError(writer, id, -32601, "Method not found.");
                    }
                }
                catch (Exception ex) {
                    // Details go to stderr only; they can contain paths and internal state.
                    Console.Error.WriteLine($"[de4dotEx] Request failed: {ex}");
                    SendError(writer, id, -32603, "Internal error");
                }
            }
        }

        static void SendResponse(TextWriter writer, JsonNode? id, object result) {
            var response = new {
                jsonrpc = "2.0",
                id = id,
                result = result
            };
            writer.WriteLine(JsonSerializer.Serialize(response));
            writer.Flush();
        }

        static void SendError(TextWriter writer, JsonNode? id, int code, string message) {
            var response = new {
                jsonrpc = "2.0",
                id = id,
                error = new { code = code, message = message }
            };
            writer.WriteLine(JsonSerializer.Serialize(response));
            writer.Flush();
        }

        static object ToolError(string text) => new {
            content = new[] { new { type = "text", text = text } },
            isError = true
        };

        static string McpRoot() {
            var root = Environment.GetEnvironmentVariable("DE4DOT_MCP_ROOT");
            return RealPath(string.IsNullOrEmpty(root) ? Directory.GetCurrentDirectory() : root);
        }

        // Full path with every symlink in it resolved, so a link can't point outside the root.
        static string RealPath(string path) {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var parts = new List<string>();
            var current = full;
            for (int guard = 0; guard < 256; guard++) {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                var target = info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true) : null;
                if (target != null) {
                    current = target.FullName;
                    continue;
                }
                var parent = Path.GetDirectoryName(current);
                if (parent == null) {
                    parts.Add(current);
                    parts.Reverse();
                    return Path.GetFullPath(Path.Combine(parts.ToArray()));
                }
                parts.Add(Path.GetFileName(current));
                current = parent;
            }
            throw new IOException("Too many levels of symbolic links: " + path);
        }

        static bool IsInside(string path, string root) {
            // root is a RealPath(), so it only ends in a separator when it is a file system root ("/").
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal);
        }

        // Path of an open file, from /proc/self/fd (Linux). null if it can't be determined.
        static string? OpenedPath(FileStream stream) {
            if (!OperatingSystem.IsLinux())
                return null;
            try {
                var fd = stream.SafeFileHandle.DangerousGetHandle().ToInt64();
                return new FileInfo($"/proc/self/fd/{fd}").LinkTarget;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                return null;
            }
        }

        // Stdio jobs: the client names paths on the server's file system, and someone with write
        // access below DE4DOT_MCP_ROOT could swap a checked path for a symlink before it is used.
        // So paths are resolved once, the input is opened once and (on Linux) the opened file is
        // verified through /proc/self/fd, de4dot works on a private copy in a job directory, and
        // the result is created with FileMode.CreateNew (O_CREAT|O_EXCL, which doesn't follow a
        // symlink) after the output directory has been resolved again.
        // Residual risk: a directory component of the output path can still be replaced between
        // that last check and the create. Don't give untrusted users write access inside the root.
        static async Task<object> ExecuteDeobfuscation(string? filePath, string? outputPath, string? extraOptions) {
            var root = McpRoot();
            if (string.IsNullOrEmpty(filePath))
                return ToolError("Error: file_path is required.");
            var input = RealPath(Path.IsPathRooted(filePath) ? filePath : Path.Combine(root, filePath));
            if (!IsInside(input, root))
                return ToolError($"Error: '{filePath}' is outside DE4DOT_MCP_ROOT ({root}).");
            if (!File.Exists(input))
                return ToolError($"Error: File '{filePath}' does not exist or is not accessible.");

            // Resolve the output's directory; the file itself must not exist (not even as a symlink).
            string outputCandidate;
            if (string.IsNullOrEmpty(outputPath))
                outputCandidate = Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + "-cleaned" + Path.GetExtension(input));
            else
                outputCandidate = Path.GetFullPath(Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(root, outputPath));
            var outputName = Path.GetFileName(outputCandidate);
            if (outputName.Length == 0 || outputName == "." || outputName == "..")
                return ToolError("Error: output_path must name a file.");
            var outputDir = RealPath(Path.GetDirectoryName(outputCandidate)!);
            var output = Path.Combine(outputDir, outputName);
            if (!IsInside(output, root))
                return ToolError($"Error: output path is outside DE4DOT_MCP_ROOT ({root}).");
            if (!Directory.Exists(outputDir))
                return ToolError("Error: the output directory does not exist.");
            if (File.Exists(output) || Directory.Exists(output) || new FileInfo(output).LinkTarget != null)
                return ToolError($"Error: output '{output}' already exists; refusing to overwrite.");

            var options = ParseOptions(extraOptions, out var optionsError);
            if (optionsError != null)
                return ToolError("Error: " + optionsError);

            await jobSlots.WaitAsync();
            try {
                var jobDir = CreateJobDir();
                try {
                    return await DeobfuscateStaged(input, filePath, output, outputDir, options, jobDir);
                }
                finally {
                    DeleteJobDir(jobDir);
                }
            }
            finally {
                jobSlots.Release();
            }
        }

        static async Task<object> DeobfuscateStaged(string input, string filePath, string output, string outputDir, ParsedOptions options, string jobDir) {
            var ext = SafeExtension(input);
            var stagedInput = Path.Combine(jobDir, "input" + ext);
            var stagedOutput = Path.Combine(jobDir, "output" + ext);
            using (var src = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var opened = OpenedPath(src);
                if (opened != null && opened != input)
                    return ToolError($"Error: '{filePath}' changed while it was being opened.");
                using var dst = new FileStream(stagedInput, FileMode.CreateNew, FileAccess.Write);
                await src.CopyToAsync(dst);
            }

            // --asm-path: references next to the original input still resolve.
            var cliArgs = BuildCliArgs(options, stagedInput, stagedOutput, Path.GetDirectoryName(input)!);
            var result = await RunDe4dotAsync(cliArgs, jobDir);
            var text = new StringBuilder();
            text.Append($"Execution completed with exit code: {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}\n");

            bool ok = result.ExitCode == 0 && File.Exists(stagedOutput);
            if (ok) {
                if (RealPath(outputDir) != outputDir || new DirectoryInfo(outputDir).LinkTarget != null)
                    return ToolError("Error: the output directory changed during the job; nothing was written.");
                try {
                    await CopyNewFile(stagedOutput, output);
                    text.Append($"Wrote {output}\n");
                }
                catch (IOException) {
                    return ToolError($"Error: could not create '{output}' (it may exist now); nothing was written.");
                }
                // Other files de4dot created (eg. dumped embedded assemblies) go next to the
                // output, under sanitized names, and never replace anything.
                foreach (var f in new DirectoryInfo(jobDir).EnumerateFiles()) {
                    if (f.FullName == stagedInput || f.FullName == stagedOutput || f.LinkTarget != null)
                        continue;
                    var dest = Path.Combine(outputDir, SafeDownloadName(f.Name));
                    try {
                        await CopyNewFile(f.FullName, dest);
                        text.Append($"Wrote {dest}\n");
                    }
                    catch (IOException) {
                        text.Append($"Skipped {f.Name}: {dest} exists or can't be created\n");
                    }
                }
            }
            text.Append("\nConsole Log:\n").Append(result.Log);

            return new {
                content = new[] {
                    new {
                        type = "text",
                        text = text.ToString()
                    }
                },
                isError = !ok
            };
        }

        static async Task CopyNewFile(string source, string dest) {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read);
            using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
            await src.CopyToAsync(dst);
        }

        #endregion
    }
}
