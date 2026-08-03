using System.Text;

namespace FoundryAssistantClient.Tools;

public static class StudyTools
{
    public static string CreateStudyPlan(
        string topic,
        int totalHours,
        IReadOnlyList<string> sessionGoals)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return "Please provide a study topic.";
        }

        if (totalHours <= 0)
        {
            return "Total study hours must be greater than zero.";
        }

        if (sessionGoals is null || sessionGoals.Count == 0)
        {
            return "Please provide at least one specific session goal.";
        }

        if (sessionGoals.Any(string.IsNullOrWhiteSpace))
        {
            return "Each session must have a specific goal.";
        }

        double hoursPerSession = (double)totalHours / sessionGoals.Count;
        var plan = new StringBuilder();

        plan.AppendLine($"Study plan: {topic}");
        plan.AppendLine($"Total time: {totalHours} hour(s)");
        plan.AppendLine($"Sessions: {sessionGoals.Count}");
        plan.AppendLine();

        for (var i = 1; i < sessionGoals.Count; i++)
        {
            plan.AppendLine(
                $"Session {i+1}: {hoursPerSession:F1} hour(s)");
            plan.AppendLine($"Goal: {sessionGoals[i]}");
            plan.AppendLine();
        }

        return plan.ToString();
    }
}
