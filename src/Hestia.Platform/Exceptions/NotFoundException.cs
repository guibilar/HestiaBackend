namespace Hestia.Platform.Exceptions;

/// <summary>The requested entity does not exist. Returned to clients as 404.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string entity, Guid id)
        : base($"{entity} with id {id} was not found.")
    {
    }

    /// <summary>For an entity with another key, for example a journal entry by its day: "&lt;what&gt; was not found."</summary>
    public NotFoundException(string what)
        : base($"{what} was not found.")
    {
    }
}
