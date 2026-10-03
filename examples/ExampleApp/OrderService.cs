namespace ExampleApp;

public sealed record OrderResult(int OrderId, string CustomerName, int Quantity, decimal Total);

public sealed record DailyReport(int OrderCount, decimal TotalRevenue);

/// <summary>A small, deliberately ordinary service class - the kind of code ProgramFlowTracer is
/// meant to instrument. Nothing in here knows or cares that tracing exists.</summary>
public sealed class OrderService
{
    private const decimal UnitPrice = 19.99m;

    private static readonly Dictionary<string, decimal> Discounts = new()
    {
        ["GOLD"] = 0.15m,
        ["SILVER"] = 0.08m
    };

    public OrderResult ProcessOrder(int orderId, string customerName, int quantity)
    {
        ValidateOrder(orderId, customerName, quantity);
        var total = CalculateTotal(quantity);
        return new OrderResult(orderId, customerName, quantity, total);
    }

    public async Task<OrderResult> ProcessOrderAsync(int orderId, string customerName, int quantity)
    {
        ValidateOrder(orderId, customerName, quantity);
        await Task.Delay(10);
        var total = CalculateTotal(quantity);
        return new OrderResult(orderId, customerName, quantity, total);
    }

    public DailyReport BuildDailyReport(IReadOnlyList<OrderResult> orders)
    {
        decimal SumTotals(IReadOnlyList<OrderResult> items)
        {
            decimal sum = 0;
            foreach (var o in items)
            {
                sum += o.Total;
            }

            return sum;
        }

        return new DailyReport(orders.Count, SumTotals(orders));
    }

    public bool TryLookupDiscount(string code, out decimal rate) => Discounts.TryGetValue(code, out rate);

    private static void ValidateOrder(int orderId, string customerName, int quantity)
    {
        if (orderId <= 0)
        {
            throw new ArgumentException("Order id must be positive.", nameof(orderId));
        }

        if (string.IsNullOrWhiteSpace(customerName))
        {
            throw new ArgumentException("Customer name is required.", nameof(customerName));
        }

        if (quantity <= 0)
        {
            throw new ArgumentException("Quantity must be positive.", nameof(quantity));
        }
    }

    private static decimal CalculateTotal(int quantity) => UnitPrice * quantity;
}
