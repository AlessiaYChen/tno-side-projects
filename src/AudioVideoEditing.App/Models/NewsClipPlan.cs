namespace AudioVideoEditing.App.Models;

internal sealed record NewsClipPlan(string Title, ClipRange Range, ClipRange? TransitionWindow = null);
