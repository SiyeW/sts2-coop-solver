using System;
using System.Collections.Generic;
using System.Linq;

namespace CoopSolver;

internal sealed record SearchResultPoint(long Visits,int? EnemyHp,int? Loss,int?[] SeatLoss,int?[] Potions);
internal sealed record SearchMemoryPoint(long Visits,long Bytes);
internal sealed record HudSearchHistory(long Visits,int LimitMb,SearchResultPoint[] Results,SearchMemoryPoint[] Memory)
{
    internal const int ResultLimit=192,MemoryLimit=1536;
}

internal sealed class SearchHistory
{
    private readonly List<SearchResultPoint> results=new();
    private readonly List<SearchMemoryPoint> memory=new();
    private object? scope;
    private long visits,scopeOffset,memoryRevision=-1;
    private HudSearchHistory? published;

    internal void Clear()
    {
        scope=null;visits=scopeOffset=0;memoryRevision=-1;results.Clear();memory.Clear();published=null;
    }

    private long Advance(object key,long work,bool continuous)
    {
        if(!ReferenceEquals(scope,key) || scopeOffset+work<visits)
        {
            if(continuous){scopeOffset=visits;memoryRevision=-1;}
            else Clear();
            scope=key;
        }
        work+=scopeOffset;
        if(work!=visits){visits=work;published=null;}
        return work;
    }

    internal long MemoryRevision(object key)=>ReferenceEquals(scope,key)?memoryRevision:-1;

    internal void RecordMemory(object key,long work,long used,long revision,bool continuous=false)
    {
        work=Advance(key,work,continuous);
        memoryRevision=revision;
        AddMemory(work,used);
    }

    private void AddMemory(long work,long used)
    {
        if(memory.Count!=0 && memory[^1].Visits==work && memory[^1].Bytes==used)return;
        // During a census the published estimate remains the current HUD
        // value. Keep both ends of a constant interval without storing each refresh.
        if(memory.Count>=2 && memory[^1].Bytes==used && memory[^2].Bytes==used)
            memory[^1]=new(work,used);
        else memory.Add(new(work,used));
        if(memory.Count>HudSearchHistory.MemoryLimit)CompactMemory();
        published=null;
    }

    internal void Record(object key,long work,HudBest? best,int? remaining,long? bytes=null,bool continuous=false)
    {
        work=Advance(key,work,continuous);
        if(best is {Outcome:"胜" or "败",Party.Length:>0})
        {
            var party=best.Party;
            int? loss=best.Outcome=="胜" && party.All(s=>s.ToEnd.HasValue)?party.Sum(s=>s.ToEnd!.Value):null;
            int? enemy=best.Outcome=="败"?remaining:null;
            if(loss.HasValue || enemy.HasValue)
            {
                var last=results.Count==0?null:results[^1];
                bool changed=last==null || last.EnemyHp!=enemy || last.Loss!=loss || last.SeatLoss.Length!=party.Length;
                for(int i=0;!changed && i<party.Length;i++)
                    changed=last!.SeatLoss[i]!=party[i].ToEnd || last.Potions[i]!=party[i].Potions;
                if(changed)
                {
                    var point=new SearchResultPoint(work,enemy,loss,party.Select(s=>s.ToEnd).ToArray(),party.Select(s=>s.Potions).ToArray());
                    if(last?.Visits==work)results[^1]=point;
                    else results.Add(point);
                    if(results.Count>HudSearchHistory.ResultLimit)CompactResults();
                    published=null;
                }
            }
        }

        if(bytes is long used)AddMemory(work,used);
        else if(memory.Count>0)AddMemory(work,memory[^1].Bytes);
    }

    internal HudSearchHistory? Snapshot(int limitMb)
    {
        if(results.Count==0 && memory.Count==0)return null;
        if(published?.LimitMb!=limitMb)published=new(visits,limitMb,results.ToArray(),memory.ToArray());
        return published;
    }

    private void CompactResults()
    {
        // Keep the first sample of each outcome, and keep recent changes in full.
        int write=1,oldest=results.Count/2;
        for(int i=1;i<oldest;i++)
            if(i%2==0 || results[i].Loss.HasValue!=results[i-1].Loss.HasValue)results[write++]=results[i];
        for(int i=oldest;i<results.Count;i++)results[write++]=results[i];
        results.RemoveRange(write,results.Count-write);
        // An oscillating result stream can otherwise retain every old transition.
        if(results.Count>HudSearchHistory.ResultLimit)results.RemoveAt(1);
    }

    private void CompactMemory()
    {
        // Preserve the peaks and troughs of old samples so reclamation remains
        // visible, and keep the most recent part of the trace in full.
        int write=1,oldest=memory.Count/2;
        for(int from=1;from<oldest;from+=4)
        {
            int low=from,high=from;
            for(int i=from+1;i<Math.Min(from+4,oldest);i++)
            {
                if(memory[i].Bytes<memory[low].Bytes)low=i;
                if(memory[i].Bytes>memory[high].Bytes)high=i;
            }
            var first=memory[Math.Min(low,high)];
            var second=memory[Math.Max(low,high)];
            memory[write++]=first;
            if(low!=high)memory[write++]=second;
        }
        for(int i=oldest;i<memory.Count;i++)memory[write++]=memory[i];
        memory.RemoveRange(write,memory.Count-write);
    }
}
