using Gaska.SelfCheckout.Domain.Sales;

namespace Gaska.SelfCheckout.Tests.Domain;

public class CartTests
{
    [Fact]
    public void Adding_same_product_increases_quantity()
    {
        var cart = new Cart();
        Assert.Equal(CartChangeResult.Added, cart.Add(TestData.Product(price: 21m)));
        Assert.Equal(CartChangeResult.Increased, cart.Add(TestData.Product(price: 21m)));
        Assert.Equal(2, Assert.Single(cart.Lines).Quantity);
        Assert.Equal(42m, cart.Total);
    }

    [Fact]
    public void Quantity_is_limited_by_stock()
    {
        var cart = new Cart();
        var p = TestData.Product(stock: 1);
        cart.Add(p);
        Assert.Equal(CartChangeResult.LimitedByStock, cart.Add(p));
        Assert.Equal(CartChangeResult.LimitedByStock, cart.SetQuantity(p.Id, 5));
        Assert.Equal(1, cart.Lines[0].Quantity);
    }

    [Fact]
    public void Out_of_stock_product_is_rejected()
    {
        var cart = new Cart();
        Assert.Equal(CartChangeResult.OutOfStock, cart.Add(TestData.Product(stock: 0)));
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public void Refresh_updates_prices_and_removes_unavailable_products()
    {
        var cart = new Cart();
        cart.Add(TestData.Product(1, 10m, 5), 3);
        cart.Add(TestData.Product(2, 20m, 5));

        var changed = cart.Refresh(new Dictionary<int, Gaska.SelfCheckout.Domain.Catalog.Product>
        {
            [1] = TestData.Product(1, 12m, 2)
        });

        Assert.Equal(2, changed.Count);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(12m, line.UnitPrice);
        Assert.Equal(2, line.Quantity);
    }

    [Fact]
    public void Quote_gives_discount_and_is_cleared_when_quantity_changes()
    {
        var cart = new Cart();
        var p = TestData.Product(1, 100m, 10);
        cart.Add(p, 2);
        Assert.True(cart.SetQuote(p.Id, 2, new PriceQuote(100m, 90m)));

        var line = cart.Lines[0];
        Assert.Equal(180m, line.Total);
        Assert.Equal(20m, line.Discount);
        Assert.Equal(10, line.DiscountPercent);
        Assert.Equal(20m, cart.Discount);

        // quantity promotions – the price has to be calculated again for the new quantity
        cart.SetQuantity(p.Id, 5);
        Assert.Null(line.Quote);
        cart.Add(p);
        Assert.Null(line.Quote);
    }

    [Fact]
    public void Quote_for_an_outdated_quantity_is_ignored()
    {
        var cart = new Cart();
        var p = TestData.Product(1, 100m, 10);
        cart.Add(p, 2);
        cart.SetQuantity(p.Id, 3);

        Assert.False(cart.SetQuote(p.Id, 2, new PriceQuote(100m, 90m)));
        Assert.Null(cart.Lines[0].Quote);
        Assert.Equal(300m, cart.Total);
    }

    [Fact]
    public void Previous_price_is_shown_while_a_changed_quantity_is_recalculated()
    {
        var cart = new Cart();
        var p = TestData.Product(1, 100m, 10);
        cart.Add(p);
        var line = cart.Lines[0];
        Assert.False(cart.HasShownPrices); // a new line has no price to show yet

        cart.SetQuote(p.Id, 1, new PriceQuote(100m, 90m));
        cart.Add(p);

        Assert.Null(line.Quote); // the sale waits for the price of 2 pcs
        Assert.True(cart.HasShownPrices);
        Assert.Equal(180m, line.ShownTotal);
        Assert.Equal(180m, cart.ShownTotal);
        Assert.Equal(20m, cart.ShownDiscount);
        Assert.Equal(200m, cart.Total);

        cart.SetQuote(p.Id, 2, new PriceQuote(100m, 80m));
        Assert.Equal(160m, cart.ShownTotal);
    }

    [Fact]
    public void Previous_prices_are_not_shown_for_another_customer()
    {
        var cart = new Cart();
        var p = TestData.Product(1, 100m, 10);
        cart.Add(p);
        cart.SetQuote(p.Id, 1, new PriceQuote(100m, 90m));

        cart.ClearQuotes();

        Assert.Null(cart.Lines[0].ShownQuote);
        Assert.False(cart.HasShownPrices);
    }
}
