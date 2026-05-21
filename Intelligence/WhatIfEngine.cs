using System;
using System.Collections.Concurrent;

namespace PropFirmGuardian.Intelligence
{
    public sealed class WhatIfEngine
    {
        private readonly ConcurrentDictionary<string, WhatIfState> _states;

        public WhatIfEngine()
        {
            _states = new ConcurrentDictionary<string, WhatIfState>(StringComparer.OrdinalIgnoreCase);
        }

        public void RecordActual(string accountName, double pnl)
        {
            WhatIfState state = _states.GetOrAdd(accountName ?? string.Empty, name => new WhatIfState());
            state.ActualPnL += pnl;
        }

        public void RecordGuardianSuggestion(string accountName, double avoidedLoss, bool followed)
        {
            WhatIfState state = _states.GetOrAdd(accountName ?? string.Empty, name => new WhatIfState());
            state.Suggestions++;
            if (followed)
                state.Followed++;
            state.GuardianPnL += avoidedLoss;
        }

        public WhatIfReport CalculateWhatIfDelta(string accountName)
        {
            WhatIfState state = _states.GetOrAdd(accountName ?? string.Empty, name => new WhatIfState());
            return new WhatIfReport(state.ActualPnL, state.GuardianPnL, state.GuardianPnL - state.ActualPnL, state.Suggestions, state.Followed);
        }

        private sealed class WhatIfState
        {
            public double ActualPnL;
            public double GuardianPnL;
            public int Suggestions;
            public int Followed;
        }
    }

    public sealed class WhatIfReport
    {
        public WhatIfReport(double actualPnL, double guardianPnL, double delta, int suggestions, int followed)
        {
            ActualPnL = actualPnL;
            GuardianPnL = guardianPnL;
            Delta = delta;
            Suggestions = suggestions;
            Followed = followed;
        }

        public double ActualPnL { get; private set; }
        public double GuardianPnL { get; private set; }
        public double Delta { get; private set; }
        public int Suggestions { get; private set; }
        public int Followed { get; private set; }
    }
}
