class User
{
    Guid Id;
    string Email;
    IEnumerable<Coin> FavoriteCoins = Enumerable.Empty<Coin>();
}