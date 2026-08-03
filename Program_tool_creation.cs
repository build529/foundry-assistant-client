using System.Text.Json;
using Azure;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using OpenAI.Responses;
using FoundryAssistantClient.Tools;

#pragma warning disable OPENAI001

const string endpoint =
    "https://agent-trials-resource.services.ai.azure.com/api/projects/agent-trials";

const string agentName = "Foundry-Learning-Agent";
const string modelDeploymentName = "gpt-4.1-mini";

if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var projectUri))
{
    Console.WriteLine("The endpoint is not a valid URL.");
    return;
}

var projectClient = new AIProjectClient(
    endpoint: projectUri,
    tokenProvider: new DefaultAzureCredential());


// ------------------------------------------------------------
// 1. Define the tool for Foundry.
// This is the description the model receives.
// It does NOT execute the function yet.
// ------------------------------------------------------------
FunctionTool studyPlanTool = ResponseTool.CreateFunctionTool(
    functionName: "create_study_plan",
    functionDescription:
        "Creates a structured study plan when the user gives a topic, "
        + "total study hours, and number of study sessions.",
    functionParameters: BinaryData.FromObjectAsJson(
        new
        {
            type = "object",
            properties = new
            {
                topic = new
                {
                    type = "string",
                    description =
                        "The topic the user wants to study. "
                        + "For example: Microsoft Foundry Agent Tools."
                },
                totalHours = new
                {
                    type = "integer",
                    description =
                        "The total number of study hours available. "
                        + "Must be greater than zero."
                },
                sessionGoals = new
                {
                    type = "array",
                    description =
                        "A list of specific, topic-related learning goals. "
                        + "Create one goal for each study session. "
                        + "Each goal must name a concrete concept, practical task, "
                        + "or expected outcome related to the user's topic. "
                        + "Do not use vague goals such as 'practice more' or "
                        + "'learn more about the topic'.",
                    items = new
                    {
                        type = "string"
                    },
                    minItems = 1
                }
            },
            required = new[]
            {
                "topic",
                "totalHours",
                "sessionGoals"
            },
            additionalProperties = false
        },
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }),
    strictModeEnabled: false);


// ------------------------------------------------------------
// 2. Create a NEW Foundry agent version with this tool.
// Do not use the old hard-coded version number here.
// ------------------------------------------------------------
var agentDefinition = new DeclarativeAgentDefinition(modelDeploymentName)
{
    Instructions =
        """
        You are Foundry Learning Agent, a patient assistant for a beginner
        learning Microsoft Foundry, Azure AI, .NET, C#, and AI-agent design.

        Explain technical concepts in simple English and proceed step by step.

        When the user asks for a study plan and provides, or can reasonably
        clarify, a topic, total hours, and number of sessions, use the
        create_study_plan tool.

        Before calling create_study_plan, create one specific session goal for
        each requested session. Each goal must be directly related to the topic
        and should include a concrete concept, hands-on action, or measurable
        learning outcome.

        Never use vague session goals such as:
        - "Learn more about the topic."
        - "Practice more."
        - "Review the material."
        - "Practise another small concept."

        For technical topics, sequence the plan logically:
        1. Core concepts and vocabulary.
        2. Configuration or implementation.
        3. Testing, debugging, comparison, review, or a small practical task.

        If File Search is available and the user asks about material in an
        uploaded document, use the document content to make the session goals
        more specific.


        Do not invent tool results. Use the tool output as the factual basis
        for the study plan.

        If necessary details are missing, ask one short clarification question
        before calling the tool.
        """
};

agentDefinition.Tools.Add(studyPlanTool);

var createdAgentVersion =
    await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
        agentName: agentName,
        options: new(agentDefinition));

Console.WriteLine(
    $"Using Foundry agent version: {createdAgentVersion.Value.Version}");
Console.WriteLine();


// ------------------------------------------------------------
// 3. Connect the Responses client to the newly created version.
// ------------------------------------------------------------
var agentReference = new AgentReference(
    name: createdAgentVersion.Value.Name,
    version: createdAgentVersion.Value.Version);

var responseClient =
    projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(agentReference);

string? previousResponseId = null;

Console.WriteLine("Foundry Learning Agent is ready.");
Console.WriteLine("Try asking for a study plan.");
Console.WriteLine("Example:");
Console.WriteLine(
    "I have 6 hours for Microsoft Foundry Agent Tools. "
    + "Create a plan with 3 sessions.");
Console.WriteLine();
Console.WriteLine("Type 'new' to begin a new conversation.");
Console.WriteLine("Type 'exit' to close the chat.");
Console.WriteLine();


// ------------------------------------------------------------
// 4. Chat loop.
// ------------------------------------------------------------
while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("See you later.");
        break;
    }

    if (input.Equals("new", StringComparison.OrdinalIgnoreCase))
    {
        previousResponseId = null;
        Console.WriteLine("New conversation started.");
        Console.WriteLine();
        continue;
    }

    try
    {
        // First request: send the user's message to the agent.
        var options = new CreateResponseOptions();

        options.InputItems.Add(
            ResponseItem.CreateUserMessageItem(input));

        if (!string.IsNullOrWhiteSpace(previousResponseId))
        {
            options.PreviousResponseId = previousResponseId;
        }

        var result = await responseClient.CreateResponseAsync(options);
        var response = result.Value;

        bool toolWasCalled;

        // ----------------------------------------------------
        // 5. Tool-call loop.
        // Keep running until Foundry no longer requests a tool.
        // ----------------------------------------------------
        do
        {
            toolWasCalled = false;

            var followUpItems = new List<ResponseItem>();

            // Include Foundry's output items, especially the function-call request.
            foreach (ResponseItem outputItem in response.OutputItems)
            {
                followUpItems.Add(outputItem);

                if (outputItem is FunctionCallResponseItem functionCall)
                {
                    if (functionCall.FunctionName == "create_study_plan")
                    {
                        Console.WriteLine("Tool requested: create_study_plan");

                        var toolOutput = ResolveStudyPlanTool(functionCall);

                        // Add the real result from your C# function.
                        followUpItems.Add(toolOutput);

                        toolWasCalled = true;
                    }
                    else
                    {
                        Console.WriteLine(
                            $"The agent requested an unsupported tool: "
                            + $"{functionCall.FunctionName}");
                    }
                }
            }

            // If a tool was called, send the function result back to Foundry.
            if (toolWasCalled)
            {
                var followUpOptions = new CreateResponseOptions();

                foreach (var item in followUpItems)
                {
                    followUpOptions.InputItems.Add(item);
                }

                var followUpResult =
                    await responseClient.CreateResponseAsync(followUpOptions);

                response = followUpResult.Value;
            }

        } while (toolWasCalled);

        var answer = response.GetOutputText();

        // Save this response ID so the next user message has conversation context.
        previousResponseId = response.Id;

        Console.WriteLine($"Agent: {answer}");
        Console.WriteLine();
    }
    catch (RequestFailedException ex)
    {
        Console.WriteLine(
            $"Azure request failed — HTTP {ex.Status}: {ex.Message}");
        Console.WriteLine();
    }
    catch (JsonException ex)
    {
        Console.WriteLine(
            $"The agent sent invalid tool arguments: {ex.Message}");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Unexpected error: {ex.Message}");
        Console.WriteLine();
    }
}


// ------------------------------------------------------------
// 6. This method executes YOUR real C# function safely.
// ------------------------------------------------------------
static FunctionCallOutputResponseItem ResolveStudyPlanTool(
    FunctionCallResponseItem functionCall)
{
    try
    {
        using JsonDocument argumentsJson =
            JsonDocument.Parse(functionCall.FunctionArguments);

        JsonElement arguments = argumentsJson.RootElement;

        string topic = arguments
            .GetProperty("topic")
            .GetString()
            ?? string.Empty;

        int totalHours = arguments
            .GetProperty("totalHours")
            .GetInt32();

        var sessionGoals = arguments
            .GetProperty("sessionGoals")
            .EnumerateArray()
            .Select(goal => goal.GetString() ?? string.Empty)
            .ToList();

        string studyPlan = StudyTools.CreateStudyPlan(
            topic: topic,
            totalHours: totalHours,
            sessionGoals: sessionGoals);

        return ResponseItem.CreateFunctionCallOutputItem(
            functionCall.CallId,
            studyPlan);
    }
    catch (Exception ex)
    {
        // Do not crash the whole agent if tool arguments are bad.
        // Return a controlled error result to the model instead.
        return ResponseItem.CreateFunctionCallOutputItem(
            functionCall.CallId,
            $"The study-plan tool could not run: {ex.Message}");
    }
}
