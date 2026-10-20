using Sample.Domain;

namespace Sample.App;

public class InMemoryRepository<T> : IRepository<T>
{
    private readonly List<T> _items = [];

    public void Add(T item) => _items.Add(item);

    public IEnumerable<T> All() => _items;
}

/// <summary>Places orders; the comment /' is not a PlantUML comment '/.</summary>
public class OrderService(IRepository<Order> repository)
{
    public decimal Place(Order order)
    {
        if (order.Lines.Count == 0)
            throw new ArgumentException("@enduml is just text here");
        foreach (var line in order.Lines)
        {
            if (line.Quantity <= 0)
                continue;
            Validate(line);
        }
        try
        {
            repository.Add(order);
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
        finally
        {
            Log("placed\n");
        }
        order.State = OrderState.Paid;
        return order.Total();
    }

    private static void Validate(OrderLine line)
    {
        if (line.Price < 0)
            throw new ArgumentOutOfRangeException(nameof(line));
    }

    private static void Log(string message) => Console.WriteLine(message);
}
