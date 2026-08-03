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
const string agentVersion = "12";

if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var projectUri))
{
    Console.WriteLine("The endpoint is not a valid URL.");
    return;
}

var projectClient = new AIProjectClient(
    endpoint: projectUri,
    tokenProvider: new DefaultAzureCredential());



// ------------------------------------------------------------
// 3. Connect the Responses client to the newly created version.
// ------------------------------------------------------------
var agentReference = new AgentReference(
    name: agentName,
    version: agentVersion);

var responseClient =
    projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(agentReference);

Console.WriteLine($"Conected to Foundry agent version: {agentVersion}");
Console.WriteLine();

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

            // This list must contain ONLY the outputs from local tools
            // that your C# program executed.

            var localToolOutputs = new List<ResponseItem>();

            // Include Foundry's output items, especially the function-call request.
            foreach (ResponseItem outputItem in response.OutputItems)
            {
                if (outputItem is FunctionCallResponseItem functionCall)
                {
                    if (functionCall.FunctionName == "create_study_plan")
                    {
                        Console.WriteLine("Tool requested: create_study_plan");
                        // Runs your local C# code and creates the result
                        // that Foundry expects from this specific call.


                        var toolOutput = ResolveStudyPlanTool(functionCall);

                        localToolOutputs.Add(toolOutput);
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
                
                var followUpOptions = new CreateResponseOptions
                {
                    //This tells Foundry: the tool results belong to the response you just gave me.
                    PreviousResponseId = response.Id
                };

                // Add ONLY the results from tools executed by this local app.
                foreach (ResponseItem toolOutput in localToolOutputs)
                {
                    followUpOptions.InputItems.Add(toolOutput);
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
