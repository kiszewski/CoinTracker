using MimeKit;
using MimeKit.Text;

class CoinUpdatesNotifierService
{
    private CoinService _coinService;
    private EmailService _emailService;
    private ILogger<CoinUpdatesNotifierService> _logger;
    private DateTimeOffset _lastReportSendDate = DateTimeOffset.MinValue;

    public CoinUpdatesNotifierService(
        CoinService coinService,
        EmailService emailService,
        ILogger<CoinUpdatesNotifierService> logger
        )
    {
        _coinService = coinService;
        _emailService = emailService;
        _logger = logger;

        var btc = new Coin()
        {
            Name = "Bitcoin",
            Code = "bitcoin"
        };

        users = new List<User> {
            new User
            {
                Name = "Leonardo",
                Email = "kiszewski1999@gmail.com",
                FavoriteCoins = new List<Coin>{ btc }
            }
        };
    }

    public async Task CheckUpdates()
    {
        var reports = await _coinService.AnalyzeCoinsDaily();

        foreach (var report in reports)
        {
            _logger.LogInformation($"Report from: {report.Coin.Code}");

            var interestedUsers = users.
                Where(u => u.FavoriteCoins.Any(coin => coin.Code == report.Coin.Code));

            foreach (var user in interestedUsers)
            {
                var mail = new SendEmailParams
                {
                    RecipientName = user.Name,
                    RecipientEmail = user.Email,
                    Subject = $"Updates on {report.Coin.Name}",
                    Body = BuildDailyHTMLReport(report)
                };

                _logger.LogInformation($"Sending report: {report.Coin} to: {user.Name}");

                await _emailService.SendEmail(mail);
            }
        }
    }

    public async Task SendWeeklyReport()
    {
        var now = DateTimeOffset.Now;
        var isFriday = now.DayOfWeek == DayOfWeek.Friday;
        var lastReportWasNotSent = Math.Abs(now.Subtract(_lastReportSendDate).TotalDays) >= 7;

        _logger.LogInformation($"Last report send date: {_lastReportSendDate} flag: {lastReportWasNotSent}");

        if (isFriday && lastReportWasNotSent)
        {
            var report = await _coinService.AnalyzeCoinWeekly(new Coin()
            {
                Name = "Bitcoin",
                Code = "bitcoin"
            });

            if (report != null)
            {
                var interestedUsers = users.Where(u => u.FavoriteCoins.Any(coin => coin.Code == report.Coin.Code));

                foreach (var user in interestedUsers)
                {
                    var mail = new SendEmailParams
                    {
                        RecipientName = user.Name,
                        RecipientEmail = user.Email,
                        Subject = $"Weekly Report: {report.Coin.Name}",
                        Body = BuildWeeklyHTMLReport(report)
                    };

                    _logger.LogInformation($"Sending weekly report: {report.Coin} to: {user.Name}");

                    await _emailService.SendEmail(mail);
                }

                _lastReportSendDate = DateTimeOffset.Now;
            }
            else
            {
                _logger.LogInformation($"No report to send");
            }
        }
    }

    private TextPart BuildDailyHTMLReport(CoinReport report)
    {
        var color = report.PercentChanged >= 0 ? "#28a745" : "#dc3545";
        var arrow = report.PercentChanged >= 0 ? "▲" : "▼";

        string html = $@"
        <html>
        <head>
            <style>
                body {{
                    font-family: 'Segoe UI', sans-serif;
                    background-color: #f9f9f9;
                    padding: 20px;
                }}
                .card {{
                    background-color: white;
                    border-radius: 12px;
                    padding: 24px;
                    max-width: 500px;
                    margin: auto;
                    box-shadow: 0 2px 10px rgba(0,0,0,0.1);
                }}
                .title {{
                    font-size: 20px;
                    font-weight: bold;
                    color: #333;
                }}
                .price {{
                    font-size: 32px;
                    font-weight: bold;
                    color: {color};
                }}
                .footer {{
                    margin-top: 20px;
                    font-size: 12px;
                    color: #777;
                }}
            </style>
        </head>
        <body>
            <div class='card'>
                <div class='title'>{report.Coin.Name} ({report.Coin.Code.ToUpper()})</div>
                <div class='price'>{arrow} ${report.NewDolarPrice:F2} USD</div>
                <p>
                    <strong>Change:</strong> {report.PercentChanged:F2}%<br/>
                    <strong>Old Price:</strong> ${report.OldDolarPrice:F2}<br/>
                    <strong>New Price:</strong> ${report.NewDolarPrice:F2}<br/>
                </p>
                <div class='footer'>
                    Last update: {report.NewDolarPriceDate:MMM dd, yyyy HH:mm}<br/>
                    Previous price: {report.OldDolarPriceDate:MMM dd, yyyy HH:mm}
                </div>
            </div>
        </body>
        </html>
        ";

        return new TextPart(TextFormat.Html) { Text = html };
    }

    private TextPart BuildWeeklyHTMLReport(CoinWeeklyReport report)
    {
        string html = $@"
        <html>
        <head>
            <style>
                body {{
                    font-family: 'Segoe UI', sans-serif;
                    background-color: #f9f9f9;
                    padding: 20px;
                }}
                .card {{
                    background-color: white;
                    border-radius: 12px;
                    padding: 24px;
                    max-width: 500px;
                    margin: auto;
                    box-shadow: 0 2px 10px rgba(0,0,0,0.1);
                }}
                .title {{
                    font-size: 20px;
                    font-weight: bold;
                    color: #333;
                }}
                .priceHigh{{
                    font-size: 32px;
                    font-weight: bold;
                    color: #28a745;
                }}
                .priceLow {{
                    font-size: 32px;
                    font-weight: bold;
                    color: #dc3545;
                }}
            </style>
        </head>
        <body>
            <div class='card'>
                <div class='title'>{report.Coin.Name} ({report.Coin.Code.ToUpper()})</div>
                <div class='priceHigh'>▲ ${report.Highest:F2} USD</div>
                <div class='priceLow'>▼ ${report.Lowest:F2} USD</div>
            </div>
        </body>
        </html>
        ";

        return new TextPart(TextFormat.Html) { Text = html };
    }

    //TODO: Get from database
    private IEnumerable<User> users;
}