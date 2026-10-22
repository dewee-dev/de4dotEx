namespace de4dot.code;

public class UnsafeCodeExecutionException : UserException
{
    public UnsafeCodeExecutionException()
        : base("Execution of arbitrary input code is disabled for security reasons. Pass --unsafe-code-execution to explicitly allow potentially DANGEROUS code execution.")
    {
    }
}
