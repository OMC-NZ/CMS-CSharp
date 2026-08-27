using CMS_CSharp.Features.Promotions.DuplicateDetection;

namespace CMS_CSharp.Features.Promotions;

internal sealed class PromotionConflictException(PromotionConflict conflict)
    : Exception("A promotion with the same models, channels, and gifts has an overlapping channel period.")
{
    public PromotionConflict Conflict { get; } = conflict;
}
