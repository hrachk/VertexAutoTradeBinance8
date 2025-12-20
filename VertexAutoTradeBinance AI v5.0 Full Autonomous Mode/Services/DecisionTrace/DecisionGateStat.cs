public sealed class DecisionGateStats
{
    public string Gate { get; set; } = "";
    public int Hits { get; set; }          // сколько раз gate сработал
    public int Blocks { get; set; }        // сколько раз заблокировал
    public decimal BlockRate =>
        Hits == 0 ? 0m : (decimal)Blocks / Hits;
}
