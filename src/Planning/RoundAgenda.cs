using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace CoopSolver;

// The file is both a replay-path arena and a FIFO. Priority indexes are just
// accelerators: evicting an index entry never deletes its pending flag.
internal sealed class RoundAgenda : IDisposable
{
    private readonly FileStream file;
    private readonly BinaryReader reader;
    private readonly BinaryWriter writer;
    private readonly MemoryStream buffer=new();
    private readonly BinaryWriter payload;
    private long oldest,prefixCursor;
    private int previousCutoff;
    private readonly SortedSet<(double Score,int Tie,long Offset,int Turn)> byPrefixQuality=new();
    private readonly SortedDictionary<int,int> pendingByTurn=new();
    private const int IndexCapacity=256;
    private readonly SortedSet<(double Score,int Tie,long Offset,int Turn)> byScore=new();
    private readonly SortedSet<(int Round,double Score,int Tie,long Offset)> byRound=new();
    internal long Pending {get;private set;}
    internal long Bytes {get;private set;}
    // Only resident stream buffers and queue indexes are charged here; Bytes is the disk arena.
    internal long ResidentBytes=>buffer.Capacity+4096+72L*(byPrefixQuality.Count+byScore.Count+byRound.Count)+48L*pendingByTurn.Count;
    private long nextSpaceCheck;
    private bool spaceLow;
    internal bool HasDiskSpace()
    {
        if(Bytes<48L*1024*1024)return true;
        if(Environment.TickCount64>=nextSpaceCheck)
        {
            nextSpaceCheck=Environment.TickCount64+5000;
            try {spaceLow=new DriveInfo(Path.GetPathRoot(file.Name)!).AvailableFreeSpace<512L*1024*1024;}
            catch(IOException){spaceLow=true;}
            catch(UnauthorizedAccessException){spaceLow=true;}
        }
        return !spaceLow;
    }

    internal RoundAgenda()
    {
        var dir=Environment.GetEnvironmentVariable("STS2_SOLVER_CASE_RESULT") is string output
            ? Path.GetDirectoryName(Path.GetFullPath(output))! : Path.GetTempPath();
        file=new FileStream(Path.Combine(dir,"coop-round-"+Guid.NewGuid().ToString("N")+".tmp"),
            FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,FileOptions.DeleteOnClose);
        reader=new BinaryReader(file,Encoding.UTF8,true);
        writer=new BinaryWriter(file,Encoding.UTF8,true);
        payload=new BinaryWriter(buffer,Encoding.UTF8,true);
    }

    internal long Append(long parent,TreeStep step,bool pending,int round,double score,int tie,double? prefixScore=null)
    {
        buffer.Position=0;
        payload.Write(pending);
        payload.Write(false); // path record
        payload.Write(parent);
        WriteMove(payload,step.Move);
        WriteDigits(payload,step.Digits);
        // Menu evidence includes the entire ordered set of physical options.
        // Compress the exact text, so replay can still compare it byte for byte.
        bool large=step.MenuEvidence.Length>=512;
        payload.Write(large);
        if(large)
        {
            using var compressed=new DeflateStream(buffer,CompressionLevel.Fastest,true);
            compressed.Write(Encoding.UTF8.GetBytes(step.MenuEvidence));
        }
        else payload.Write(step.MenuEvidence);
        return Commit(pending,round,score,tie,prefixScore);
    }

    internal void DeferCursor(long path,ActionFrontier frontier,int firstSeat,int round,double score,int tie)
    {
        buffer.Position=0;
        payload.Write(true);
        payload.Write(true); // cursor record
        payload.Write(path);
        payload.Write(firstSeat);
        frontier.Save(payload);
        Commit(true,round,score,tie);
    }

    internal ActionFrontier? ReadCursor(ref long id,out int? firstSeat)
    {
        firstSeat=null;
        if(id<0)return null;
        file.Position=id+2*sizeof(int)+sizeof(byte);
        if(!reader.ReadBoolean())return null;
        id=reader.ReadInt64();
        firstSeat=reader.ReadInt32();
        return ActionFrontier.Load(reader);
    }

    private long Commit(bool pending,int round,double score,int tie,double? prefixScore=null)
    {
        int size=checked((int)buffer.Position);
        long start=file.Seek(0,SeekOrigin.End);
        writer.Write(checked(2*sizeof(int)+size));
        writer.Write(round);
        writer.Write(buffer.GetBuffer(),0,size);
        Bytes=start+2*sizeof(int)+size;
        if(pending)
        {
            Pending++;
            pendingByTurn[round]=pendingByTurn.GetValueOrDefault(round)+1;
            if(prefixScore.HasValue)
            {
                byPrefixQuality.Add((prefixScore.Value,tie,start,round));
                if(byPrefixQuality.Count>IndexCapacity)byPrefixQuality.Remove(byPrefixQuality.Min);
            }
            byScore.Add((score,tie,start,round));
            if(byScore.Count>IndexCapacity)byScore.Remove(byScore.Min);
            byRound.Add((round,score,tie,start));
            if(byRound.Count>IndexCapacity)byRound.Remove(byRound.Min);
        }
        return start;
    }

    private bool Claim(long offset)
    {
        file.Position=offset+sizeof(int);
        int turn=reader.ReadInt32();
        if(!reader.ReadBoolean())return false;
        file.Position=offset+2*sizeof(int);
        if(--pendingByTurn[turn]==0)pendingByTurn.Remove(turn);
        writer.Write(false);
        Pending--;
        return true;
    }

    // Each call performs bounded work, including skipping already claimed
    // records. The next scheduling slice resumes at the same FIFO position.
    internal bool Take(int lane,out long offset)
    {
        offset=-1;
        if(Pending==0)return false;
        if(lane==1 && byScore.Count!=0)
        {
            var item=byScore.Max;byScore.Remove(item);offset=item.Offset;
            return Claim(offset);
        }
        if(lane==2 && byRound.Count!=0)
        {
            var item=byRound.Max;byRound.Remove(item);offset=item.Offset;
            return Claim(offset);
        }
        for(int i=0;i<32 && oldest<file.Length;i++)
        {
            offset=oldest;
            file.Position=offset;
            oldest+=reader.ReadInt32();
            if(Claim(offset))return true;
        }
        return false;
    }

    internal bool HasPrefix(int cutoff)=>pendingByTurn.Count!=0 && pendingByTurn.First().Key<=cutoff;

    // These are the same pending rows as the unrestricted queue. Both readers
    // claim the same bit, so advancing either reader cannot duplicate a job.
    internal bool TakePrefix(int lane,int cutoff,out long offset)
    {
        offset=-1;
        if(!HasPrefix(cutoff))return false;
        if(previousCutoff!=cutoff){previousCutoff=cutoff;prefixCursor=oldest;}
        if(lane!=0)
        {
            var option=byPrefixQuality.Reverse().FirstOrDefault(x=>x.Turn<=cutoff);
            if(option!=default){byPrefixQuality.Remove(option);offset=option.Offset;if(Claim(offset))return true;}
        }
        if(lane==1)
        {
            var candidate=byScore.Reverse().FirstOrDefault(x=>x.Turn<=cutoff);
            if(candidate!=default){byScore.Remove(candidate);offset=candidate.Offset;if(Claim(offset))return true;}
        }
        if(lane==2)
        {
            var eligible=byRound.GetViewBetween((int.MinValue,double.NegativeInfinity,int.MinValue,long.MinValue),
                (cutoff,double.PositiveInfinity,int.MaxValue,long.MaxValue));
            if(eligible.Count!=0)
            {
                var item=eligible.Max;byRound.Remove(item);offset=item.Offset;if(Claim(offset))return true;
            }
        }
        prefixCursor=Math.Max(prefixCursor,oldest);
        for(int i=0;i<64 && prefixCursor<Bytes;i++)
        {
            offset=prefixCursor;file.Position=offset;
            prefixCursor+=reader.ReadInt32();
            if(reader.ReadInt32()<=cutoff && Claim(offset))return true;
        }
        return false;
    }

    internal IEnumerable<(long Id,long Parent,bool Cursor,bool Pending)> Rows()
    {
        long end=file.Length;
        for(long offset=0;offset<end;)
        {
            file.Position=offset;
            int size=reader.ReadInt32();
            reader.ReadInt32(); // scheduling turn
            bool pending=reader.ReadBoolean(),cursor=reader.ReadBoolean();
            long parent=reader.ReadInt64();
            yield return (offset,parent,cursor,pending);
            offset+=size;
        }
    }

    internal TreeStep ReadStep(long offset,out long parent)
    {
        file.Position=offset;
        long end=offset+reader.ReadInt32();
        reader.ReadInt32(); // scheduling turn
        reader.ReadBoolean();
        if(reader.ReadBoolean())throw new InvalidDataException("Cursor used as a path");
        parent=reader.ReadInt64();
        var move=ReadMove(reader);
        var digits=ReadDigits(reader);
        string evidence;
        if(reader.ReadBoolean())
        {
            using var block=new MemoryStream(reader.ReadBytes(checked((int)(end-file.Position))));
            using var inflater=new DeflateStream(block,CompressionMode.Decompress);
            using var text=new StreamReader(inflater,Encoding.UTF8);
            evidence=text.ReadToEnd();
        }
        else evidence=reader.ReadString();
        return new(move,digits,evidence);
    }

    internal static void WriteMove(BinaryWriter writer,Move m)
    {
        writer.Write(m.Seat);writer.Write((byte)m.Kind);writer.Write(m.Slot);writer.Write(m.Key);
        writer.Write(m.Target.HasValue);
        if(m.Target.HasValue)writer.Write(m.Target.Value);
        writer.Write(m.PotionChoice);
    }

    internal static Move ReadMove(BinaryReader reader)
    {
        int seat=reader.ReadInt32();
        var kind=(MoveKind)reader.ReadByte();
        int slot=reader.ReadInt32();
        string key=reader.ReadString();
        uint? target=reader.ReadBoolean()?reader.ReadUInt32():null;
        return new Move(seat,kind,slot,key,target,reader.ReadInt32());
    }

    internal static void WriteDigits(BinaryWriter writer,int[] digits)
    {
        writer.Write(digits.Length);
        foreach(int digit in digits)writer.Write(digit);
    }

    internal static int[] ReadDigits(BinaryReader reader)
    {
        int count=reader.ReadInt32();

        var digits=count==0 ? Array.Empty<int>() : new int[count];
        for(int i=0;i<count;i++)digits[i]=reader.ReadInt32();
        return digits;
    }

    public void Dispose()
    {
        payload.Dispose();buffer.Dispose();writer.Dispose();reader.Dispose();file.Dispose();
    }
}
