namespace CoopSolver;

// One retained-tree census for opening, live and choice search. 0 means unlimited.
internal sealed class SearchMemoryBudget
{
    internal readonly SearchTreeMemory Trees=new();
    private volatile bool parked;
    private long TreeSize()=>Trees.Read(!parked);
    internal volatile bool ReclaimOnPressure=true;
    internal bool Waiting(bool active=false)
    {
        int mb=SearchCapacity.MemoryLimitMb;
        parked=mb>0 && TreeSize()>=mb*1_000_000L;
        Trees.AtLimit=parked;
        if(parked && active && ReclaimOnPressure)Trees.RequestReclamation(mb*1_000_000L);
        return parked;
    }
}
