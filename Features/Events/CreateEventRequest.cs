using System.Text.Json;

namespace CMS_CSharp.Features.Events;

internal sealed class CreateEventRequest
{
    public string? Name { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public bool RequiresImei { get; init; } = true;
    public bool RequiresChannel { get; init; } = true;
    public bool RequiresDelivery { get; init; }
    public IReadOnlyList<string>? Models { get; init; } = [];
    public IReadOnlyList<string>? ChannelCodes { get; init; } = [];
    public IReadOnlyList<EventSectionInput>? Sections { get; init; } = [];
    public IReadOnlyList<EventUploadInput>? Uploads { get; init; } = [];
}

internal sealed record EventSectionInput(
    string? Title, int SortOrder, IReadOnlyList<EventFieldInput>? Fields);

internal sealed record EventFieldInput(
    string? Key, string? Label, string? Type, string? Placeholder,
    bool Required, int SortOrder, JsonElement? Validation,
    IReadOnlyList<EventOptionInput>? Options);

internal sealed record EventOptionInput(string? Value, string? Label, int SortOrder);
internal sealed record EventUploadInput(string? Key, string? Label);
internal sealed record CreateEventResult(bool Success, long EventId, string SlugUrl);
internal sealed class EventValidationException(string message) : Exception(message);
