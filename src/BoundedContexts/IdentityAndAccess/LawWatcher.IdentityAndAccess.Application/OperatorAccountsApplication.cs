using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IdentityAndAccess.Contracts;
using LawWatcher.IdentityAndAccess.Domain.OperatorAccounts;

namespace LawWatcher.IdentityAndAccess.Application;

public sealed record RegisterOperatorAccountCommand(
    Guid OperatorId,
    string Email,
    string DisplayName,
    string Password,
    IReadOnlyCollection<string> Permissions) : Command;

public sealed record UpdateOperatorAccountCommand(
    Guid OperatorId,
    string DisplayName,
    IReadOnlyCollection<string> Permissions) : Command;

public sealed record ResetOperatorPasswordCommand(
    Guid OperatorId,
    string NewPassword) : Command;

public sealed record DeactivateOperatorAccountCommand(
    Guid OperatorId) : Command;

public sealed record OperatorAccountReadModel(
    Guid Id,
    string Email,
    string DisplayName,
    string PasswordHash,
    IReadOnlyCollection<string> Permissions,
    bool IsActive,
    DateTimeOffset RegisteredAtUtc);

public interface IOperatorAccountRepository
{
    Task<OperatorAccount?> GetAsync(OperatorAccountId id, CancellationToken cancellationToken);

    Task SaveAsync(OperatorAccount account, CancellationToken cancellationToken);
}

public interface IOperatorAccountReadRepository
{
    Task<IReadOnlyCollection<OperatorAccountReadModel>> GetOperatorsAsync(CancellationToken cancellationToken);

    Task<OperatorAccountReadModel?> GetByIdAsync(Guid operatorId, CancellationToken cancellationToken);

    Task<OperatorAccountReadModel?> GetByEmailAsync(string email, CancellationToken cancellationToken);
}

public interface IOperatorAccountProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public interface IOperatorPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string passwordHash);
}

public enum OperatorAuthenticationDecision
{
    UnknownEmail = 0,
    InactiveOperator = 1,
    InvalidPassword = 2,
    Authorized = 3
}

public enum OperatorAccessDecision
{
    UnknownOperator = 0,
    InactiveOperator = 1,
    MissingPermission = 2,
    Authorized = 3
}

public sealed record OperatorAuthenticationResult(
    OperatorAuthenticationDecision Decision,
    Guid? OperatorId,
    string? Email,
    string? DisplayName,
    IReadOnlyCollection<string> Permissions);

public sealed record OperatorAccessResult(
    OperatorAccessDecision Decision,
    Guid? OperatorId,
    string? Email,
    string? DisplayName,
    IReadOnlyCollection<string> Permissions);

public sealed class OperatorAccountsCommandService(
    IOperatorAccountRepository repository,
    IOperatorAccountReadRepository readRepository,
    IOperatorAccountProjection projection,
    IOperatorPasswordHasher passwordHasher)
{
    public async Task RegisterAsync(RegisterOperatorAccountCommand command, CancellationToken cancellationToken)
    {
        var operatorId = new OperatorAccountId(command.OperatorId);
        var existing = await repository.GetAsync(operatorId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Operator account '{command.OperatorId}' has already been registered.");
        }

        var normalizedEmail = NormalizeEmail(command.Email);
        var existingEmail = await readRepository.GetByEmailAsync(normalizedEmail, cancellationToken);
        if (existingEmail is not null)
        {
            throw new InvalidOperationException($"Operator email '{normalizedEmail}' has already been registered.");
        }

        var account = OperatorAccount.Register(
            operatorId,
            OperatorEmail.Create(normalizedEmail),
            OperatorDisplayName.Create(command.DisplayName),
            PasswordHash.FromStored(passwordHasher.Hash(command.Password)),
            command.Permissions.Select(OperatorPermission.Of).ToArray(),
            command.RequestedAtUtc);

        await SaveAndProjectAsync(account, cancellationToken);
    }

    public async Task UpdateAsync(UpdateOperatorAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await repository.GetAsync(new OperatorAccountId(command.OperatorId), cancellationToken)
            ?? throw new InvalidOperationException($"Operator account '{command.OperatorId}' was not found.");

        account.Update(
            OperatorDisplayName.Create(command.DisplayName),
            command.Permissions.Select(OperatorPermission.Of).ToArray(),
            command.RequestedAtUtc);
        await SaveAndProjectAsync(account, cancellationToken);
    }

    public async Task ResetPasswordAsync(ResetOperatorPasswordCommand command, CancellationToken cancellationToken)
    {
        var account = await repository.GetAsync(new OperatorAccountId(command.OperatorId), cancellationToken)
            ?? throw new InvalidOperationException($"Operator account '{command.OperatorId}' was not found.");

        account.ResetPassword(
            PasswordHash.FromStored(passwordHasher.Hash(command.NewPassword)),
            command.RequestedAtUtc);
        await SaveAndProjectAsync(account, cancellationToken);
    }

    public async Task DeactivateAsync(DeactivateOperatorAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await repository.GetAsync(new OperatorAccountId(command.OperatorId), cancellationToken)
            ?? throw new InvalidOperationException($"Operator account '{command.OperatorId}' was not found.");

        account.Deactivate(command.RequestedAtUtc);
        await SaveAndProjectAsync(account, cancellationToken);
    }

    private async Task SaveAndProjectAsync(OperatorAccount account, CancellationToken cancellationToken)
    {
        var pendingEvents = account.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        await repository.SaveAsync(account, cancellationToken);
        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }

    private static string NormalizeEmail(string value)
    {
        return OperatorEmail.Create(value).Value;
    }
}

public sealed class OperatorAccountsQueryService(IOperatorAccountReadRepository repository)
{
    public async Task<IReadOnlyList<OperatorAccountResponse>> GetOperatorsAsync(CancellationToken cancellationToken)
    {
        var operators = await repository.GetOperatorsAsync(cancellationToken);
        return operators
            .OrderBy(@operator => @operator.Email, StringComparer.OrdinalIgnoreCase)
            .Select(MapToResponse)
            .ToArray();
    }

    public async Task<OperatorAccountResponse?> GetOperatorAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var @operator = await repository.GetByIdAsync(operatorId, cancellationToken);
        return @operator is null ? null : MapToResponse(@operator);
    }

    private static OperatorAccountResponse MapToResponse(OperatorAccountReadModel @operator)
    {
        return new OperatorAccountResponse(
            @operator.Id,
            @operator.Email,
            @operator.DisplayName,
            @operator.Permissions
                .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            @operator.IsActive,
            @operator.RegisteredAtUtc);
    }
}

public sealed class OperatorAuthenticationService(
    IOperatorAccountReadRepository repository,
    IOperatorPasswordHasher passwordHasher)
{
    public async Task<OperatorAuthenticationResult> AuthenticateAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        if (normalizedEmail is null)
        {
            return new OperatorAuthenticationResult(
                OperatorAuthenticationDecision.UnknownEmail,
                null,
                null,
                null,
                Array.Empty<string>());
        }

        var @operator = await repository.GetByEmailAsync(normalizedEmail, cancellationToken);
        if (@operator is null)
        {
            return new OperatorAuthenticationResult(
                OperatorAuthenticationDecision.UnknownEmail,
                null,
                null,
                null,
                Array.Empty<string>());
        }

        if (!@operator.IsActive)
        {
            return new OperatorAuthenticationResult(
                OperatorAuthenticationDecision.InactiveOperator,
                @operator.Id,
                @operator.Email,
                @operator.DisplayName,
                @operator.Permissions);
        }

        if (!passwordHasher.Verify(password, @operator.PasswordHash))
        {
            return new OperatorAuthenticationResult(
                OperatorAuthenticationDecision.InvalidPassword,
                @operator.Id,
                @operator.Email,
                @operator.DisplayName,
                @operator.Permissions);
        }

        return new OperatorAuthenticationResult(
            OperatorAuthenticationDecision.Authorized,
            @operator.Id,
            @operator.Email,
            @operator.DisplayName,
            @operator.Permissions);
    }

    private static string? NormalizeEmail(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || !trimmed.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }
}

public sealed class OperatorAccessService(IOperatorAccountReadRepository repository)
{
    public async Task<OperatorAccessResult> AuthorizeAsync(
        Guid operatorId,
        string requiredPermission,
        CancellationToken cancellationToken)
    {
        var normalizedPermission = NormalizePermission(requiredPermission, nameof(requiredPermission), "Required permission");
        var @operator = await repository.GetByIdAsync(operatorId, cancellationToken);
        if (@operator is null)
        {
            return new OperatorAccessResult(
                OperatorAccessDecision.UnknownOperator,
                null,
                null,
                null,
                Array.Empty<string>());
        }

        if (!@operator.IsActive)
        {
            return new OperatorAccessResult(
                OperatorAccessDecision.InactiveOperator,
                @operator.Id,
                @operator.Email,
                @operator.DisplayName,
                @operator.Permissions);
        }

        if (!@operator.Permissions.Contains(normalizedPermission, StringComparer.OrdinalIgnoreCase))
        {
            return new OperatorAccessResult(
                OperatorAccessDecision.MissingPermission,
                @operator.Id,
                @operator.Email,
                @operator.DisplayName,
                @operator.Permissions);
        }

        return new OperatorAccessResult(
            OperatorAccessDecision.Authorized,
            @operator.Id,
            @operator.Email,
            @operator.DisplayName,
            @operator.Permissions);
    }

    private static string NormalizePermission(string value, string paramName, string label)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return normalized;
    }
}
