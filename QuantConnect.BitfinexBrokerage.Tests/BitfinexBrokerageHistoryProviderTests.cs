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
        // the last two bools in params order are:
        // 1) whether or not 'GetHistory' is expected to return an empty result
        // 2) whether or not an ArgumentException is expected to be thrown during 'GetHistory' execution
        private static TestCaseData[] History()
        {
            TestGlobals.Initialize();
            return new[]
            {
                // valid
                new TestCaseData(StaticSymbol, Resolution.Minute, TimeSpan.FromMinutes(2), false, false, TickType.Trade),
                new TestCaseData(StaticSymbol, Resolution.Hour, Time.OneDay, false, false, TickType.Trade),
                new TestCaseData(StaticSymbol, Resolution.Daily, TimeSpan.FromDays(15), false, false, TickType.Trade),

                // invalid data types, no error, empty result
                new TestCaseData(StaticSymbol, Resolution.Minute, Time.OneMinute, false, true, TickType.Quote),
                new TestCaseData(StaticSymbol, Resolution.Tick, Time.OneMinute, false, true, TickType.Quote),
                new TestCaseData(StaticSymbol, Resolution.Minute, Time.OneMinute, false, true, TickType.OpenInterest),

                // invalid resolution, no error, empty result
                new TestCaseData(StaticSymbol, Resolution.Tick, TimeSpan.FromSeconds(15), false, true, TickType.Trade),
                new TestCaseData(StaticSymbol, Resolution.Second, Time.OneMinute, false, true, TickType.Trade),

                // invalid period, no error, empty result
                new TestCaseData(StaticSymbol, Resolution.Daily, TimeSpan.FromDays(-15), true, false, TickType.Trade),

                new TestCaseData(Symbol.Create("XYZ", SecurityType.Crypto, Market.Bitfinex),
                    Resolution.Daily, TimeSpan.FromDays(15), false, true, TickType.Trade),

                // invalid security type, no error, empty result
                new TestCaseData(Symbols.EURUSD, Resolution.Daily, TimeSpan.FromDays(15), false, true, TickType.Trade)
            };
        }

        [Test]
        [TestCaseSource(nameof(History))]
        public void GetsHistory(Symbol symbol, Resolution resolution, TimeSpan period, bool shouldBeEmpty, bool notSupported, TickType tickType)
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

            var history = brokerage.GetHistory(request);

            if (notSupported)
            {
                Assert.IsNull(history);
            }
            else if (shouldBeEmpty)
            {
                Assert.IsEmpty(history);
            }
            else
            {
                var historyList = history.ToList();

                foreach (TradeBar bar in historyList)
                {
                    Log.Trace("{0}: {1} - O={2}, H={3}, L={4}, C={5}", bar.Time, bar.Symbol, bar.Open, bar.High, bar.Low, bar.Close);
                }

                Log.Trace("Data points retrieved: " + historyList.Count);

                Assert.IsTrue(historyList.Count > 0);
            }
        }
    }
}
