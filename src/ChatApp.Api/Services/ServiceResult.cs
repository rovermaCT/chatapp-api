namespace ChatApp.Api.Services;

public sealed record ServiceError(int StatusCode, string Title, string Detail)
{
    public static ServiceError BadRequest(string detail) => new(StatusCodes.Status400BadRequest, "Bad request", detail);

    public static ServiceError NotFound(string detail) => new(StatusCodes.Status404NotFound, "Not found", detail);

    public static ServiceError Conflict(string detail) => new(StatusCodes.Status409Conflict, "Conflict", detail);
}

public sealed class ServiceResult<T>
{
    private ServiceResult(T? value, ServiceError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public ServiceError? Error { get; }

    public static implicit operator ServiceResult<T>(T value) => new(value, null);

    public static implicit operator ServiceResult<T>(ServiceError error) => new(default, error);
}
