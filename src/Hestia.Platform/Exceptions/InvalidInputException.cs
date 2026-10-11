namespace Hestia.Platform.Exceptions;

/// <summary>
/// A request value that cannot be used. Returned to clients as 400, with the same shape as model validation
/// (the message is under <see cref="Field"/>).
/// </summary>
public class InvalidInputException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}
