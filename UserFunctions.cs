using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace UserFunctionApp;

// The table entity that is stored in Azure Table storage
public class UserEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "User"; // all users share one partition
    public string RowKey { get; set; } = string.Empty; // the user id
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Surname { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

// Shape of the JSON the client sends when creating a user
public class UserInput
{
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public string? Email { get; set; }
}

public class UserFunctions
{
    private readonly ILogger<UserFunctions> _logger;
    private readonly TableClient _table;

    public UserFunctions(ILogger<UserFunctions> logger)
    {
        _logger = logger;

        // Uses the storage connection string from local.settings.json (or app settings in Azure)
        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        _table = new TableClient(connectionString, "Users");
        _table.CreateIfNotExists();
    }

    // 1. Create a user from JSON and store it in the table
    [Function("CreateUser")]
    public async Task<IActionResult> CreateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "users")] HttpRequest req)
    {
        UserInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<UserInput>(
                req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new { error = "Request body is not valid JSON." });
        }

        if (input is null ||
            string.IsNullOrWhiteSpace(input.Name) ||
            string.IsNullOrWhiteSpace(input.Surname) ||
            string.IsNullOrWhiteSpace(input.Email))
        {
            return new BadRequestObjectResult(new { error = "Name, Surname and Email are required." });
        }

        var entity = new UserEntity
        {
            RowKey = Guid.NewGuid().ToString(),
            Name = input.Name,
            Surname = input.Surname,
            Email = input.Email
        };

        await _table.AddEntityAsync(entity);
        _logger.LogInformation("Created user {UserId}", entity.RowKey);

        return new ObjectResult(ToDto(entity)) { StatusCode = StatusCodes.Status201Created };
    }

    // 2. Get one user by id
    [Function("GetUser")]
    public async Task<IActionResult> GetUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users/{id}")] HttpRequest req,
        string id)
    {
        try
        {
            var response = await _table.GetEntityAsync<UserEntity>("User", id);
            return new OkObjectResult(ToDto(response.Value));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new NotFoundObjectResult(new { error = $"User with id {id} was not found." });
        }
    }

    // 3. Get all users
    [Function("GetAllUsers")]
    public async Task<IActionResult> GetAllUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users")] HttpRequest req)
    {
        var users = new List<object>();

        await foreach (var user in _table.QueryAsync<UserEntity>(u => u.PartitionKey == "User"))
        {
            users.Add(ToDto(user));
        }

        return new OkObjectResult(users);
    }

    // Returns only the fields we want to show (hides ETag, PartitionKey and so on)
    private static object ToDto(UserEntity u) => new
    {
        id = u.RowKey,
        name = u.Name,
        surname = u.Surname,
        email = u.Email
    };
}
