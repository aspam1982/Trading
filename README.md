# Trading Research Toolkit

`Trading` is a .NET 8 / WPF portfolio project for market research, strategy backtesting, trading-robot prototyping, futures arbitrage analysis, AI-assisted news analysis, and secure local credential management.

The repository is organized as a single Visual Studio solution with several focused desktop applications and one shared library. It was built as a research and engineering workspace rather than a packaged trading product: the emphasis is on data handling, backtesting workflows, visual analysis, API integration, and practical tooling around trading experiments.

> This repository is for software engineering and research demonstration purposes only. It is not financial advice and is not intended for unattended production trading without additional risk controls, monitoring, audit logging, and operational safeguards.

## What This Project Demonstrates

- C# / .NET 8 desktop development with WPF.
- Multi-project solution architecture.
- Shared domain and infrastructure code through `CommonClasses`.
- Integration with T-Invest API for instruments, candles, market data, and trading-related workflows.
- Historical candle loading, local data processing, and strategy backtesting.
- ScottPlot-based visualization of price series, equity curves, optimization windows, validation periods, and strategy results.
- Futures arbitrage research workflow: data collection, offline analysis, and robot mode.
- AI-assisted news analysis with ChatGPT and DeepSeek integration points.
- Secure local secret lookup through Windows Credential Manager.
- Git-ready project hygiene with generated build artifacts excluded from source control.

## Solution Structure

| Project | Type | Purpose |
| --- | --- | --- |
| `CommonClasses` | Class library | Shared infrastructure: T-Invest helpers, historical data models, robot base classes, strategy utilities, credential management, and optimization helpers. |
| `StrategyBacktester` | WPF app | Interactive strategy research and backtesting UI. Includes grid strategy, EMA crossover, pair/futures correlation research, orderbook-density visualizations, and parameter search experiments. |
| `BondSelectorWPF` | WPF app | Conservative OFZ-IN bond selector focused on inflation-linked Russian government bonds, liquidity filters, YTM, duration, turnover, and carry/roll-down style analysis. |
| `RoboTrader` | WPF app | Trading-robot host and monitoring UI for robot settings, portfolio/deposit history, and strategy execution experiments. |
| `RoboFutureArbitr` | WPF app | Futures arbitrage workspace with three launch modes: data collection, collected-data analysis, and trading robot mode. |
| `RoboAINewsReader` | WPF app | News and candle forecasting workspace with ChatGPT/DeepSeek-based news analysis and candle forecast tooling. |
| `CredentialsEditor` | WPF app | Windows Credential Manager editor for listing, adding, editing, and deleting local credentials used by the other projects. |

## StrategyBacktester Highlights

`StrategyBacktester` is the main research UI. It contains several independent windows for testing and visualizing trading ideas:

- `GridStrategy`: grid-style backtest on a selected ticker and date range.
- `EmaCrossoverBacktest`: EMA crossover strategy with ticker selection, date range, optimization interval, delay interval, and validation interval.
- `PairsCorrelation`: stock/futures relationship research with selectable instruments and test period.
- `OrderbookDensity`: price-density map based on candle ranges, volume weighting, deviation, and decay.
- `OrderbookTimeDensity`: time-density map showing where price spent more time, with configurable decay.
- `GrokAdvice`: RSI mean-reversion parameter search.
- `GrokAdvice1`: moving-average / ATR crossover parameter search.

Several backtest windows use a walk-forward style layout: the full selected price period is shown on the upper chart, while the equity curve and test result are shown from the beginning of the optimization period through the end of the validation period. Optimization and validation intervals are highlighted visually to make the split between fitting and checking explicit.

## Screenshots And Backtest Results

Screenshots should be placed in:

```text
docs/screenshots/
```

Recommended naming pattern:

```text
docs/screenshots/strategybacktester-ema-crossover-sber-2024.png
docs/screenshots/strategybacktester-grid-gazp-2024.png
docs/screenshots/strategybacktester-pairs-sber-si-2024.png
docs/screenshots/robofuturearbitr-analysis-example.png
docs/screenshots/roboainewsreader-news-analysis-example.png
```

When adding a screenshot, include:

- Strategy or tool name.
- Instrument and period.
- Main parameters.
- What the upper chart shows.
- What the equity/result chart shows.
- Optimization interval and validation interval, if applicable.
- Short interpretation of the result.
- Important caveat, for example liquidity, commissions, overfitting risk, or narrow market depth.

Example Markdown block:

```md
### EMA Crossover Backtest - SBER

![EMA Crossover Backtest](docs/screenshots/strategybacktester-ema-crossover-sber-2024.png)

This run tests an EMA crossover strategy on SBER hourly candles. The upper chart shows the full loaded price period. The lower chart shows the equity curve from the start of the optimization interval through the end of the validation interval. The green band marks the optimization window, while the red band marks the validation window.

The example is useful for checking whether a parameter set survives a delayed out-of-sample period. It should not be interpreted as a production-ready trading rule without additional checks for commissions, slippage, liquidity, and parameter stability.
```

Suggested sections for screenshots:

### Backtest Result Gallery

Screenshots will be added here as the project is prepared for publication.

<!--
### EMA Crossover Backtest - Example

![EMA Crossover Backtest](docs/screenshots/strategybacktester-ema-crossover-example.png)

Explanation:
- Instrument:
- Data interval:
- Optimization interval:
- Validation interval:
- Key parameters:
- Result:
- Notes:
-->

## Credentials And Secrets

API tokens are not intended to be stored in source files or JSON configuration files. Runtime code reads secret values from Windows Credential Manager by name.

Use `CredentialsEditor` to manage local secrets:

- list credentials by `UserName` filter;
- add a new credential;
- view or edit an existing credential;
- delete a selected credential after confirmation.

Typical credential names used by the applications are configuration values such as `InvestTestAccount`, `ChatGPTApiKey`, and `DeepSeekApiKey`. These are names of local Windows credentials, not the secret values themselves.

Before publishing or pushing changes, check that no real API keys are present in source files, config files, logs, screenshots, or sample data.

## Build

Requirements:

- Windows
- .NET 8 SDK
- Visual Studio 2022 or another IDE capable of building WPF projects

Restore and build:

```powershell
dotnet restore .\Trading.sln
dotnet build .\Trading.sln
```

Some projects depend on local credentials or market-data availability at runtime, but the solution should build without committing secrets.

## Repository Hygiene

Generated build artifacts are intentionally excluded:

- `bin/` libraries and executables;
- `obj/` intermediate MSBuild files;
- Visual Studio local files such as `.vs/` and `*.user`;
- logs and temporary files.

JSON files are not globally ignored because this repository uses JSON for reference and test data. If JSON data is generated under `bin/Debug`, only the relevant data files should be committed. Build-generated JSON files such as `*.deps.json`, `*.runtimeconfig.json`, and `obj/project.assets.json` should generally stay out of the repository.

Longer term, test and sample data should preferably live in explicit folders such as `Data/`, `TestData/`, or `Samples/` rather than under `bin/Debug`.

## Status

This is a portfolio-oriented research repository. The codebase is useful for demonstrating trading-domain engineering, backtesting UI workflows, and integration with external APIs, but it is not presented as a finished commercial trading platform.

