/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Linq;
using NodaTime;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Brokerages.Bitfinex;
using QuantConnect.Util;
using QuantConnect.Data.Market;

namespace QuantConnect.Tests.Brokerages.Bitfinex
{
    [TestFixture]
    public partial class BitfinexBrokerageTests
    {
        private static TestCaseData[] History()
        {
            TestGlobals.Initialize();
            return new[]
            {
                // valid
                new TestCaseData(StaticSymbol, Resolution.Minute, TimeSpan.FromMinutes(30), false, TickType.Trade),
                new TestCaseData(StaticSymbol, Resolution.Hour, Time.OneDay, false, TickType.Trade),
                new TestCaseData(StaticSymbol, Resolution.Daily, TimeSpan.FromDays(15), false, TickType.Trade),

                // invalid data types, no error, null result
                new TestCaseData(StaticSymbol, Resolution.Minute, Time.OneMinute, true, TickType.Quote),
                new TestCaseData(StaticSymbol, Resolution.Tick, Time.OneMinute, true, TickType.Quote),
                new TestCaseData(StaticSymbol, Resolution.Minute, Time.OneMinute, true, TickType.OpenInterest),

                // invalid resolution, no error, null result
                new TestCaseData(StaticSymbol, Resolution.Tick, TimeSpan.FromSeconds(15), true, TickType.Trade),
                new TestCaseData(StaticSymbol, Resolution.Second, Time.OneMinute, true, TickType.Trade),

                // invalid period, no error, null result
                new TestCaseData(StaticSymbol, Resolution.Daily, TimeSpan.FromDays(-15), true, TickType.Trade),

                new TestCaseData(Symbol.Create("XYZ", SecurityType.Crypto, Market.Bitfinex),
                    Resolution.Daily, TimeSpan.FromDays(15), true, TickType.Trade),

                // invalid security type, no error, null result
                new TestCaseData(Symbols.EURUSD, Resolution.Daily, TimeSpan.FromDays(15), true, TickType.Trade)
            };
        }

        [Test]
        [TestCaseSource(nameof(History))]
        public void GetsHistory(Symbol symbol, Resolution resolution, TimeSpan period, bool notSupported, TickType tickType)
        {
            var brokerage = (BitfinexBrokerage)Brokerage;
            var now = DateTime.UtcNow;
            var request = new HistoryRequest(now.Add(-period),
                now,
                LeanData.GetDataType(resolution, tickType),
                symbol,
                resolution,
                SecurityExchangeHours.AlwaysOpen(TimeZones.Utc),
                DateTimeZone.Utc,
                Resolution.Minute,
                false,
                false,
                DataNormalizationMode.Adjusted,
                tickType);

            var history = brokerage.GetHistory(request)?.ToList();

            if (notSupported)
            {
                Assert.IsNull(history);
                return;
            }

            foreach (TradeBar bar in history)
            {
                Log.Trace("{0}: {1} - O={2}, H={3}, L={4}, C={5}", bar.Time, bar.Symbol, bar.Open, bar.High, bar.Low, bar.Close);
            }

            Log.Trace("Data points retrieved: " + history.Count);

            Assert.IsTrue(history.Count > 0);
        }
    }
}
