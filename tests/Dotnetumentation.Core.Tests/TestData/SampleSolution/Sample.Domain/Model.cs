namespace Sample.Domain;

/// <summary>Stores items of one type.</summary>
public interface IRepository<T>
{
    void Add(T item);
    IEnumerable<T> All();
}

public abstract class Entity
{
    public int Id { get; init; }
}

/// <summary>An order of a <see cref="Customer"/>.</summary>
public sealed class Order : Entity
{
    public List<OrderLine> Lines { get; } = [];
    public Customer? Customer { get; set; }
    public OrderState State { get; set; }

    public decimal Total() => Lines.Sum(l => l.Price * l.Quantity);
}

public record OrderLine(string Product, decimal Price, int Quantity);

public class Customer : Entity
{
    public string Name { get; set; } = "";
}

public enum OrderState
{
    New,
    Paid,
    Shipped,
}
