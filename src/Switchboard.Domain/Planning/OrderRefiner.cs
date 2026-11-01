namespace Switchboard.Domain.Planning;

/// <summary>
/// Tries other orders of the goals: each goal in turn is moved to another place in the current order (to the front only, when there are many goals), the
/// goals are released and refilled in that order, and the arrangement is kept only if less need is left unmet or, with the same need met, the cost is
/// lower. It repairs what the earliest-deadline order gets wrong, a goal that took a cell another needed (the design's "refine": a goal released and
/// refilled with first call on the capacity is the move-to-front case), never raises the cost, and stops after <c>planner.refine_passes</c> passes or
/// when a pass finds nothing. A pass is at most n squared fills of tens of cells: milliseconds.
/// </summary>
internal sealed class OrderRefiner(PlanContext ctx, CapacityLedger ledger, List<GoalWork> allWorks)
{
    /// <summary>Up to this many goals every order is tried (4! = 24 fills); above it the single moves below.</summary>
    private const int MaxPermutedGoals = 4;

    /// <summary>Above this many goals only "first call" moves are tried: the work grows with the square of the goal count, and no installation has this many.</summary>
    private const int MaxReorderedGoals = 8;

    /// <param name="works">The goals that may be reordered (the critical ones keep their place).</param>
    public void Refine(List<GoalWork> works)
    {
        if (works.Count < 2 || ctx.Input.Parameters.RefinePasses <= 0)
        {
            return;
        }

        var order = new List<GoalWork>(works);
        var full = order.Count <= MaxReorderedGoals;
        var best = Score();
        if (order.Count <= MaxPermutedGoals)
        {
            // A handful of goals (a car and a battery, two trips): every order is tried, so what the order can repair, it repairs.
            var kept = order;
            foreach (var trial in Permutations(order))
            {
                ctx.Cancel.ThrowIfCancellationRequested();
                var saved = Save(kept);
                foreach (var w in kept)
                {
                    w.Release(ledger);
                }

                foreach (var w in trial)
                {
                    GoalPlacer.Place(ctx, ledger, w);
                }

                var score = Score();
                if (Better(score, best))
                {
                    best = score;
                    kept = trial;
                }
                else
                {
                    Restore(saved, kept);
                }
            }

            order = kept;
        }

        for (var pass = 0; pass < ctx.Input.Parameters.RefinePasses; pass++)
        {
            var improved = false;
            for (var i = 0; i < order.Count; i++)
            {
                for (var j = 0; j < order.Count; j++)
                {
                    if (j == i || (!full && j != 0))
                    {
                        continue;
                    }

                    ctx.Cancel.ThrowIfCancellationRequested();
                    var trial = new List<GoalWork>(order);
                    var moved = trial[i];
                    trial.RemoveAt(i);
                    trial.Insert(j, moved);
                    var saved = Save(order);
                    foreach (var w in order)
                    {
                        w.Release(ledger);
                    }

                    foreach (var w in trial)
                    {
                        GoalPlacer.Place(ctx, ledger, w);
                    }

                    var score = Score();
                    if (Better(score, best))
                    {
                        best = score;
                        order = trial;
                        improved = true;
                    }
                    else
                    {
                        Restore(saved, order);
                    }
                }
            }

            if (!improved)
            {
                break;
            }
        }
    }

    private static bool Better((double Shortfall, double Cost) score, (double Shortfall, double Cost) best) =>
        score.Shortfall < best.Shortfall - Planner.Tolerance || (Math.Abs(score.Shortfall - best.Shortfall) <= Planner.Tolerance && score.Cost < best.Cost - 1e-9);

    private static IEnumerable<List<GoalWork>> Permutations(List<GoalWork> items)
    {
        // Lexicographic by position in the current order, so the result is a function of the order and not of anything else.
        var indexes = Enumerable.Range(0, items.Count).ToArray();
        IEnumerable<int[]> Recurse(int[] prefix, List<int> rest)
        {
            if (rest.Count == 0)
            {
                yield return prefix;
                yield break;
            }

            foreach (var index in rest.ToList())
            {
                var next = rest.Where(i => i != index).ToList();
                foreach (var permutation in Recurse([.. prefix, index], next))
                {
                    yield return permutation;
                }
            }
        }

        foreach (var permutation in Recurse([], [.. indexes]))
        {
            yield return [.. permutation.Select(i => items[i])];
        }
    }

    private (double Shortfall, double Cost) Score() => (allWorks.Sum(w => w.Shortfall), allWorks.Sum(w => w.Cost));

    private (CapacityLedger Ledger, List<Alloc>[] Allocs, bool[] Escalated) Save(List<GoalWork> works) =>
        (ledger.Clone(), [.. works.Select(w => new List<Alloc>(w.Allocs))], [.. works.Select(w => w.Escalated)]);

    private void Restore((CapacityLedger Ledger, List<Alloc>[] Allocs, bool[] Escalated) saved, List<GoalWork> works)
    {
        ledger.Restore(saved.Ledger);
        for (var i = 0; i < works.Count; i++)
        {
            works[i].Allocs = saved.Allocs[i];
            works[i].MarkEscalated(saved.Escalated[i]);
        }
    }
}
