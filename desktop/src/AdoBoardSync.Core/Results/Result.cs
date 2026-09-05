namespace AdoBoardSync.Core.Results;

public enum ErrorKind
{
    Validation,
    NotFound,
    Authorization,
    RateLimited,
    SourceFailure,
    Conflict
}

public sealed record Error(ErrorKind Kind, string Code, string SafeMessage)
{
    public static Error Validation(string code, string message) =>
        new(ErrorKind.Validation, code, message);

    public static Error NotFound(string code, string message) =>
        new(ErrorKind.NotFound, code, message);

    public static Error Authorization(string code, string message) =>
        new(ErrorKind.Authorization, code, message);

    public static Error RateLimited(string code, string message) =>
        new(ErrorKind.RateLimited, code, message);

    public static Error SourceFailure(string code, string message) =>
        new(ErrorKind.SourceFailure, code, message);

    public static Error Conflict(string code, string message) =>
        new(ErrorKind.Conflict, code, message);
}

public readonly struct Result<T>
{
    private readonly T? _value;

    // Set only by the value constructor, so an uninitialised `default(Result<T>)` —
    // which has no Error and therefore reports IsSuccess — is still distinguishable
    // from a success that deliberately carries null.
    private readonly bool _hasValue;

    private Result(T value)
    {
        _value = value;
        _hasValue = true;
        Error = null;
    }

    private Result(Error error)
    {
        _value = default;
        _hasValue = false;
        Error = error;
    }

    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    /// <summary>
    /// The value a success carries. Success is decided by <see cref="Error"/> alone —
    /// never by whether the value is null. A <c>Result&lt;string?&gt;</c> reports
    /// "this source holds no token" as a SUCCESS carrying null, which is a different
    /// answer from "this source failed", and an extra null check here would collapse
    /// the two and throw on the ordinary case.
    /// </summary>
    public T Value => Error is not null
        ? throw new InvalidOperationException("Result has no value when it is a failure.")
        : _hasValue
            ? _value!
            : throw new InvalidOperationException(
                "This Result was never given a value. A default(Result<T>) carries neither a value "
                + "nor an error; construct it from one or the other.");

    public Error? Error { get; }

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);
}
