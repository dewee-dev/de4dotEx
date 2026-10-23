# Security Policy

## Reporting a Vulnerability

If you believe you have discovered a security vulnerability in de4dotEx, please report it through [G DATA's vulnerability reporting process](https://www.gdata.de/en/report-security-vulnerability).

**Please do not disclose security vulnerabilities through public GitHub issues, discussions, or pull requests.**

When reporting a vulnerability, please include:
- The affected de4dotEx version or commit.
- The operating system and relevant environment details.
- The steps required to reproduce the issue.
- A proof of concept, sample assembly, or other supporting files, if applicable.
- The potential security impact and any relevant prerequisites, such as command-line options or server configuration.

Please avoid including unrelated confidential information in reports or attachments.

## Scope

The following guidelines describe which issues are considered relevant to security reporting for de4dotEx. They are intended to help distinguish security vulnerabilities from ordinary bugs; they do not guarantee that every report will be accepted as a security issue.

### In Scope

Examples of potentially security-relevant issues include:

- **Unintended code execution:** Execution of code embedded in a processed assembly without the user explicitly enabling or invoking functionality intended to execute that code.
- **Unauthorized filesystem writes:** Vulnerabilities that allow a crafted input to create or modify files outside the output locations explicitly specified by the user.
- **Unauthorized filesystem access:** Vulnerabilities that allow a crafted input to read or modify files or directories beyond those intended to be accessible for the requested operation.
- **Command-line argument injection:** Vulnerabilities that allow untrusted input to alter command execution or bypass intended security restrictions.
- **MCP server security:** Authentication or authorization failures, unintended code execution, or unauthorized filesystem access through the MCP interfaces.
- **Security boundary bypasses:** Vulnerabilities that bypass explicit restrictions intended to prevent unsafe operations or unauthorized access.
- **Vulnerable dependencies**: Vulnerabilities in third-party dependencies that affect de4dotEx and have a **demonstrable security impact** in the context of its use. Reports concerning dependencies without a demonstrated impact on de4dotEx are generally out of scope.

Other issues may also qualify if they have a demonstrable security impact.

### Out of Scope

The following are generally not considered security vulnerabilities for this project:

- **Denial of service through resource consumption:** Excessive CPU or memory consumption, hangs, or crashes caused by specially crafted or unusually complex input files. These can be reported as ordinary bugs.
- **Intentionally enabled code execution:** Execution of code as an expected consequence of explicitly enabling `--allow-unsafe-execution` or invoking functionality that dynamically decrypts strings by executing code from the processed assembly.
- **Expected deobfuscation behavior:** Differences in output, incomplete deobfuscation, unsupported obfuscation techniques, dysfunctional output assemblies, or incorrect results that do not have a demonstrable security impact.
- **Ordinary crashes and malformed-input handling:** Exceptions or crashes caused by invalid or unsupported input, unless they demonstrate a separate security vulnerability.
- **Malicious input and resulting output**: Malicious behavior preserved or revealed during deobfuscation, including output assemblies that remain malicious or become easier to analyze, provided that this behavior originates from the input assembly and does not result from a separate vulnerability in de4dotEx.
- **Issues requiring prior control of the execution environment:** Attacks that require an attacker to already have the ability to execute arbitrary code with the same privileges as the affected process, absent a meaningful privilege escalation or security boundary bypass.
