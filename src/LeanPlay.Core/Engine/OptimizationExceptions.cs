namespace LeanPlay.Core.Engine;

public sealed class ProfileValidationException : Exception
{
    public ProfileValidationException(string message)
        : base(message)
    {
    }
}

public sealed class OptimizationActivationException : Exception
{
    public OptimizationActivationException(string message)
        : base(message)
    {
    }

    public OptimizationActivationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
