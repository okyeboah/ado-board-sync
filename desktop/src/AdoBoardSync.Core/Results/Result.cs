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

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
    }

    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    public T Value => IsSuccess && _value is not null
        ? _value
        : throw new InvalidOperationException("Result has no value when it is a failure.");

    public Error? Error { get; }

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);
}
