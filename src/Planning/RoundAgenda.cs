using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    private long oldest;
    private const int IndexCapacity=256;
    private readonly SortedSet<(double Score,int Tie,long Offset)> byScore=new();
    private readonly SortedSet<(int Round,double Score,int Tie,long Offset)> byRound=new();
    internal long Pending {get;private set;}
    internal long Bytes=>file.Length;

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

    internal long Append(long parent,TreeStep step,bool pending,int round,double score,int tie)
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
        return Commit(pending,round,score,tie);
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
        file.Position=id+sizeof(int)+sizeof(byte);
        if(!reader.ReadBoolean())return null;
        id=reader.ReadInt64();
        firstSeat=reader.ReadInt32();
        return ActionFrontier.Load(reader);
    }

    private long Commit(bool pending,int round,double score,int tie)
    {
        int size=checked((int)buffer.Position);
        long start=file.Seek(0,SeekOrigin.End);
        writer.Write(checked(sizeof(int)+size));
        writer.Write(buffer.GetBuffer(),0,size);
        if(pending)
        {
            Pending++;
            byScore.Add((score,tie,start));
            if(byScore.Count>IndexCapacity)byScore.Remove(byScore.Min);
            byRound.Add((round,score,tie,start));
            if(byRound.Count>IndexCapacity)byRound.Remove(byRound.Min);
        }
        return start;
    }

    private bool Claim(long offset)
    {
        file.Position=offset+sizeof(int);
        if(!reader.ReadBoolean())return false;
        file.Position=offset+sizeof(int);
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

    internal IEnumerable<(long Id,long Parent,bool Cursor,bool Pending)> Rows()
    {
        long end=file.Length;
        for(long offset=0;offset<end;)
        {
            file.Position=offset;
            int size=reader.ReadInt32();
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
