namespace AzurePRTemplateGenerator;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

internal class Program
{
    private const string Token = "{YOUR_AZURE_DEVOPS_PERSONAL_ACCESS_TOKEN}";
    private const string Organization = "{ORGANIZATION_NAME}";
    private const string Project = "{PROJECT_NAME}";
    private const string OrgUrl = $"https://dev.azure.com/{Organization}/{Project}";

    private static readonly List<string> RepositoriesToExclude =
    [
        "{REPOSITORY_NAME1}",
        "{REPOSITORY_NAME2}"
    ];

    private const string SourceBranch = "feature/add-pr-template";
    private const string TargetBranch = "dev";

    private const string FileName = "{FILE_NAME}.md";
    private const string TemplateFilePath = $".azuredevops/pull_request_template/{FileName}";


    private static async Task Main()
    {
        // Authentication
        // Initialize HttpClient
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{Token}")));

        // Retrieve repositories
        var repositories = await GetRepositories(client);


        // Generate and create pull requests
        foreach (var repo in repositories)
        {
            var fileExists = await CheckIfFileExists(client, repo);
            if (fileExists) continue;

            await GeneratePullRequestTemplate(repo);

            //Create branch with changes
            var latestCommitId = await CreateBranch(client, repo);

            //Push changes to source branch (you can use Git commands or Azure DevOps API to push changes)
            await CreateCommitAndPushChanges(client, repo, latestCommitId);

            //Create pull request for the given repository
            await CreatePullRequest(client, repo);
        }
    }

    private static async Task<List<string>> GetRepositories(HttpClient client)
    {
        const string url = $"{OrgUrl}/_apis/git/repositories?api-version=6.0";
        var response = await client.GetAsync(url);

        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync();
        var repositories = JsonSerializer.Deserialize<RepositoryResponse>(responseBody)?.Root ?? [];

        return repositories
            .Select(repo => repo.Name)
            .Except(RepositoriesToExclude, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static async Task<bool> CheckIfFileExists(HttpClient client, string repo)
    {
        // Get the contents of the repository
        var url = $"{OrgUrl}/_apis/git/repositories/{repo}/items?path={Uri.EscapeDataString(TemplateFilePath)}&api-version=6.0";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        _ = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        // Handle other status codes
        throw new Exception($"{repo}: Failed to check file existence: {response.StatusCode}.");
    }

    private static async Task GeneratePullRequestTemplate(string repo)
    {
        // Generate pull request template for the given repository
        const string templateContent = """
                                       ## Description
                                       """;

        await File.WriteAllTextAsync(FileName, templateContent);
        Console.WriteLine($"{repo}: Pull request template generated for repository.");
    }

    private static async Task<string> CreateBranch(HttpClient client, string repo)
    {
        // Get the latest commit ID of the base branch
        var baseBranchUrl = $"{OrgUrl}/_apis/git/repositories/{repo}/refs?filter=heads/dev&api-version=6.0";
        var baseBranchResponse = await client.GetAsync(baseBranchUrl);
        baseBranchResponse.EnsureSuccessStatusCode();
        var baseBranchResponseBody = await baseBranchResponse.Content.ReadAsStringAsync();
        var baseBranchInfo = JsonSerializer.Deserialize<BranchInfoResponse>(baseBranchResponseBody)?.Root ?? [];
        var latestCommitId = baseBranchInfo[0].Id; // The latest commit ID of the base branch

        // Create the new branch from the latest commit of the base branch
        var url = $"{OrgUrl}/_apis/git/repositories/{repo}/refs?api-version=6.0";
        var branchData = new List<object>
        {
            new
            {
                name = $"refs/heads/{SourceBranch}",
                oldObjectId = "0000000000000000000000000000000000000000",
                newObjectId = latestCommitId
            }
        };

        var branchContent = new StringContent(JsonSerializer.Serialize(branchData), Encoding.UTF8, "application/json");
        var branchResponse = await client.PostAsync(url, branchContent);
        branchResponse.EnsureSuccessStatusCode();
        _ = await branchResponse.Content.ReadAsStringAsync();

        Console.WriteLine($"{repo}: Branch '{SourceBranch}' created.");

        return latestCommitId;
    }

    private static async Task CreateCommitAndPushChanges(HttpClient client, string repo, string latestCommitId)
    {
        var url = $"{OrgUrl}/_apis/git/repositories/{repo}/pushes?api-version=6.0";
        var commitData = new
        {
            refUpdates = new List<object>
            {
                new
                {
                    name = $"refs/heads/{SourceBranch}",
                    oldObjectId = latestCommitId
                }

            },
            commits = new List<object>
            {
                new
                {
                    comment = "Added PR template for the repository",
                    changes = new List<object>
                    {
                        new
                        {
                            changeType = "add",
                            item = new
                            {
                                path = TemplateFilePath
                            },
                            newContent = new
                            {
                                content = await File.ReadAllTextAsync(FileName),
                                contentType = "rawtext"
                            }
                        }
                    }
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(commitData), Encoding.UTF8, "application/json");
        var commitResponse = await client.PostAsync(url, content);
        commitResponse.EnsureSuccessStatusCode();
        _ = await commitResponse.Content.ReadAsStringAsync();

        Console.WriteLine($"{repo}: Commit with new template is pushed.");
    }

    private static async Task CreatePullRequest(HttpClient client, string repo)
    {

        var url = $"{OrgUrl}/_apis/git/repositories/{repo}/pullrequests?api-version=6.0";
        var pullRequestData = new
        {
            sourceRefName = $"refs/heads/{SourceBranch}",
            targetRefName = $"refs/heads/{TargetBranch}",
            title = "Added PR template for the repository",
            description = "This pull request adds PR template for the repository."
        };

        var content = new StringContent(JsonSerializer.Serialize(pullRequestData), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);

        response.EnsureSuccessStatusCode();
        Console.WriteLine($"{repo}: Pull request with changes is created.");
    }
}

internal record BranchInfoResponse(List<BranchInfoResponse.Branch> Root)
{
    [JsonPropertyName("value")] public List<Branch> Root { get; set; } = Root;

    internal record Branch(string Id)
    {
        [JsonPropertyName("objectId")] public string Id { get; set; } = Id;

    }
}

internal record RepositoryResponse(List<RepositoryResponse.Repository> Root)
{
    [JsonPropertyName("value")] public List<Repository> Root { get; set; } = Root;

    internal record Repository(string Name)
    {
        [JsonPropertyName("name")] public string Name { get; set; } = Name;
    }
}