using Microsoft.Data.Sqlite;

namespace BlazorApp1.Services;

public interface IOrderStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SaveAsync(OrderRecord order, CancellationToken cancellationToken);
    Task<OrderRecord?> FindByAuthorityAsync(string authority, CancellationToken cancellationToken);
    Task<OrderRecord?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    Task SetPaymentStatusAsync(Guid id, string status, string? authority, string? referenceId, CancellationToken cancellationToken);
}

public sealed record OrderRecord(
    Guid Id,
    string CustomerName,
    string Mobile,
    string Address,
    string ItemsJson,
    long SubtotalToman,
    long DeliveryFeeToman,
    long AmountToman,
    long AmountRial,
    DateTimeOffset CreatedAt,
    string PaymentStatus,
    string? Authority,
    string? ReferenceId);

public sealed class SqliteOrderStore(string connectionString) : IOrderStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Orders (
                Id TEXT NOT NULL PRIMARY KEY,
                CustomerName TEXT NOT NULL,
                Mobile TEXT NOT NULL,
                Address TEXT NOT NULL,
                ItemsJson TEXT NOT NULL,
                SubtotalToman INTEGER NOT NULL DEFAULT 0,
                DeliveryFeeToman INTEGER NOT NULL DEFAULT 0,
                AmountToman INTEGER NOT NULL,
                AmountRial INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                PaymentStatus TEXT NOT NULL,
                Authority TEXT NULL UNIQUE,
                ReferenceId TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var columnsCommand = connection.CreateCommand())
        {
            columnsCommand.CommandText = "PRAGMA table_info(Orders);";
            await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        foreach (var column in new[] { "SubtotalToman", "DeliveryFeeToman" })
        {
            if (!existingColumns.Contains(column))
            {
                await using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = $"ALTER TABLE Orders ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0;";
                await alterCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    public async Task SaveAsync(OrderRecord order, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Orders
                (Id, CustomerName, Mobile, Address, ItemsJson, SubtotalToman, DeliveryFeeToman, AmountToman, AmountRial, CreatedAt, PaymentStatus, Authority, ReferenceId)
            VALUES
                ($id, $customerName, $mobile, $address, $itemsJson, $subtotalToman, $deliveryFeeToman, $amountToman, $amountRial, $createdAt, $paymentStatus, $authority, $referenceId);
            """;
        AddOrderParameters(command, order);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<OrderRecord?> FindByAuthorityAsync(string authority, CancellationToken cancellationToken) =>
        FindAsync("Authority = $key", "$key", authority, cancellationToken);

    public Task<OrderRecord?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        FindAsync("Id = $key", "$key", id.ToString("D"), cancellationToken);

    public async Task SetPaymentStatusAsync(
        Guid id,
        string status,
        string? authority,
        string? referenceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Orders
            SET PaymentStatus = $paymentStatus,
                Authority = COALESCE($authority, Authority),
                ReferenceId = COALESCE($referenceId, ReferenceId)
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$paymentStatus", status);
        command.Parameters.AddWithValue("$authority", (object?)authority ?? DBNull.Value);
        command.Parameters.AddWithValue("$referenceId", (object?)referenceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("The order was not found while updating its payment status.");
        }
    }

    private async Task<OrderRecord?> FindAsync(
        string predicate,
        string parameterName,
        string key,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, CustomerName, Mobile, Address, ItemsJson, SubtotalToman, DeliveryFeeToman, AmountToman, AmountRial, CreatedAt, PaymentStatus, Authority, ReferenceId
            FROM Orders
            WHERE {predicate}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(parameterName, key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OrderRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            DateTimeOffset.Parse(reader.GetString(9)),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private static void AddOrderParameters(SqliteCommand command, OrderRecord order)
    {
        command.Parameters.AddWithValue("$id", order.Id.ToString("D"));
        command.Parameters.AddWithValue("$customerName", order.CustomerName);
        command.Parameters.AddWithValue("$mobile", order.Mobile);
        command.Parameters.AddWithValue("$address", order.Address);
        command.Parameters.AddWithValue("$itemsJson", order.ItemsJson);
        command.Parameters.AddWithValue("$subtotalToman", order.SubtotalToman);
        command.Parameters.AddWithValue("$deliveryFeeToman", order.DeliveryFeeToman);
        command.Parameters.AddWithValue("$amountToman", order.AmountToman);
        command.Parameters.AddWithValue("$amountRial", order.AmountRial);
        command.Parameters.AddWithValue("$createdAt", order.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$paymentStatus", order.PaymentStatus);
        command.Parameters.AddWithValue("$authority", (object?)order.Authority ?? DBNull.Value);
        command.Parameters.AddWithValue("$referenceId", (object?)order.ReferenceId ?? DBNull.Value);
    }
}
