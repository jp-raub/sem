using Sem.Web.Core;

namespace Sem.Web.Components.Shared;

// Called only after the participation service returns its committed result.
public static class PublicAnswerRefreshPolicy
{
    public static bool SavedAnswers(string kind, ParticipationResult result) =>
        (kind is "Register" or "Spectate" or "Answers") &&
        result.Items.Any(item => item.Success &&
            item.Code is "Registered" or "SpectatorRegistered" or "AnswersUpdated");
}
