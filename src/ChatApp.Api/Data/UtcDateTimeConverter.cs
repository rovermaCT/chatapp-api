using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChatApp.Api.Data;

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.ToUniversalTime(),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
