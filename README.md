[Uploading TRADELEDGER_SPEC.md…]()
# TradeLedger (C# / .NET)

## 1. Purpose
Build a **free, self-hosted options trading journal** that replaces TraderSync for an options-focused trader using:
- Credit spreads
- Broken-wing butterflies (BWBs)
- Mixed daily + swing trading

The system must:
- Support **multiple brokers** (Charles Schwab, tastytrade)
- Start **CSV-first** (reliable, zero API friction)
- Be designed to **upgrade to broker APIs later** with minimal refactoring
- Produce actionable analytics (expectancy, drawdown, DTE performance)

---

## 2. Guiding Principles
- **CSV is the source of truth** (API is a later convenience)
- **Execution-level data first**, journal trades derived second
- **Consistency > perfection** for spread grouping
- Analytics optimized for **options risk**, not equity curves

---

## 3. Technology Stack (Locked)
- .NET 8
- Blazor Server (local-first, fast iteration)
- EF Core + SQLite (single-file DB)
- MudBlazor (UI)
- Chart.js (via JSInterop) or MudBlazor Charts

---

## 4. High-Level Architecture

CSV Files
→ Importer (per broker)
→ Execution Store (raw fills)
→ Normalizer (option legs)
→ Grouping Engine
→ TradeGroups (journal trades)
→ Metrics Engine
→ Dashboard UI

---

## 5. Core Domain Models

### Execution (Raw Fill)
- Broker (Schwab / tastytrade)
- Account
- Time
- Symbol
- Quantity
- Price
- Fees / commissions
- Side (Buy/Sell)
- OrderId (when available)

### OptionLeg (Normalized)
- Underlying
- Expiration
- Strike
- Right (Call / Put)
- Quantity

### TradeGroup (Journal Trade)
- StrategyType (CreditSpread / BWB)
- Setup
- OpenTime / CloseTime
- DTE at entry
- Net P/L
- Max Drawdown

---

## 6. Grouping Logic

### Credit Spread
- 2 legs
- Same expiration & right
- Opposite quantities
- Net credit

### Broken-Wing Butterfly
- 3 legs
- Same expiration & right
- Quantity ratio ~1:-2:1
- Asymmetric wings

---

## 7. Metrics
- Expectancy
- Win rate
- Avg win / loss
- Profit factor
- Max drawdown

Dimensions:
- Strategy
- Setup
- DTE bucket
- Day

---

## 8. Killer Dashboard
1. Expectancy by Setup
2. Win rate vs Avg win/loss
3. Daily P/L heatmap
4. Max drawdown by Setup
5. Profit factor by DTE bucket

---

## 9. Phases
**Phase 1:** CSV MVP  
**Phase 2:** Broker APIs

---

## 10. Phase 1 Done Criteria
- Both brokers import correctly
- Spreads & BWBs grouped correctly
- Dashboard answers expectancy & risk questions

---

This document is the **single source of truth** for TradeLedger.
