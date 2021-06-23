﻿using System;
using System.Text;
using System.IO;
using Pyrrho.Common;
using Pyrrho.Level3;
using Pyrrho.Level4;
using System.Data;
using System.Threading;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2021
//
// This software is without support and no liability for damage consequential to use.
// You can view and test this code, and use it subject for any purpose.
// You may incorporate any part of this code in other software if its origin 
// and authorship is suitably acknowledged.
// All other use or distribution or the construction of any product incorporating 
// this technology requires a license from the University of the West of Scotland.

namespace Pyrrho.Level2
{
    public abstract class IOBase
    {
        public class Buffer
        {
            public const int Size = 1024;
            public long start;
            public byte[] buf;
            public int len;
            public int pos;
            public Buffer()
            {
                buf = new byte[Size];
                pos = 0;
            }
            public Buffer(long s, int n)
            {
                buf = new byte[Size];
                pos = 0;
                start = s;
                len = n;
            }
        }
        public Buffer buf = new Buffer();
        public virtual int GetBuf(long s)
        {
            throw new NotImplementedException();
        }
        public virtual void PutBuf()
        {
            throw new NotImplementedException();
        }
        public virtual int ReadByte()
        {
            throw new NotImplementedException();
        }
        public virtual void WriteByte(byte value)
        {
            throw new NotImplementedException();
        }
    }
    public abstract class WriterBase : IOBase
    {
        public void PutInt(int? n)
        {
            if (n == null)
                throw new PEException("Null PutInt");
            PutInteger(new Integer(n.Value));
        }
        internal void PutInteger(Integer b)
        {
            var m = b.Length;
            WriteByte((byte)m);
            for (int j = 0; j < m; j++)
                WriteByte(b[j]);
        }
        public void PutLong(long n)
        {
            PutInteger(new Integer(n));
        }
        public void PutString(string s)
        {
            var cs = Encoding.UTF8.GetBytes(s);
            PutInt(cs.Length);
            for (var i = 0; i < cs.Length; i++)
                WriteByte(cs[i]);
        }
        public void PutBytes(byte[] b)
        {
            PutInt(b.Length);
            for (var i = 0; i < b.Length; i++)
                WriteByte(b[i]);
        }
    }
    public class Writer : WriterBase
    {
        public Stream file; // shared with Reader(s)
        public long seg = -1;    // The SSegment uid for the start of a Commit once roles are defined
        internal BTree<long, long> uids = BTree<long, long>.Empty; // used for movement of DbObjects
        internal BTree<long, RowSet> rss = BTree<long, RowSet>.Empty; // ditto RowSets
        // fixups: unknownolduid -> referer -> how->bool
        internal BTree<long,BTree<long,BTree<long,bool>>> fixup 
            = BTree<long, BTree<long, BTree<long, bool>>>.Empty;
        internal long curs = -1;
        public long segment;  // the most recent PTransaction/PTriggeredAction written
        public long srcPos,oldStmt,stmtPos; // for Fixing uids
        internal BList<Rvv> rvv= BList<Rvv>.Empty;
        BList<byte[]> prevBufs = BList<byte[]>.Empty;
        internal Context cx; // access the database we are writing to
        internal Writer(Context c,Stream f)
        {
            cx = c;
            file = f;
        }
        public long Length => file.Length + buf.pos;
        public override void PutBuf()
        {
            file.Seek(0, SeekOrigin.End);
            for (var b=prevBufs.First();b!=null;b=b.Next())
            {
                var bf = b.value();
                file.Write(bf, 0, Buffer.Size);
            }
            prevBufs = BList<byte[]>.Empty;
            file.Write(buf.buf, 0, buf.pos);
            buf.pos = 0;
        }
        public override void WriteByte(byte value)
        {
            if (buf.pos >= Buffer.Size)
            {
                prevBufs += buf.buf;
                buf.buf = new byte[Buffer.Size];
                buf.pos = 0;
            }
            buf.buf[buf.pos++] = value;
        }
        internal Ident PutIdent(Ident id)
        {
            if (id == null || id.ident=="")
            {
                PutString("");
                return null;
            }
            var r = new Ident(id.ident,Length);
            PutString(id.ident);
            return r;
        }
        internal long Fix(long pos)
        {
            if (uids.Contains(pos)) 
                return uids[pos];
            if (cx.parse==ExecuteStatus.Prepare && pos>PyrrhoServer.Preparing)
            {
                var r = cx.db.nextStmt;
                cx.db += (Database.NextStmt, r+1);
                uids += (pos, r);
                return r;
            }
            if (pos>=Transaction.Executables && pos<oldStmt)
            {
                uids += (pos, ++stmtPos);
                return stmtPos;
            }
            if (pos>Transaction.Analysing && pos<Transaction.Executables)
            {
                uids += (pos, ++srcPos);
                return srcPos;
            }
            return pos;
        }
        internal long Fix1(long pos)
        {
            return uids.Contains(pos)?uids[pos] : pos;
        }
        internal Ident Fix(Ident id)
        {
            if (id == null)
                return null;
            var p = Fix(id.iix);
            if (p == id.iix)
                return id;
            return new Ident(id.ident, p);
        }
        /// <summary>
        /// Not to be used for ObInfo
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        internal DBObject Fixed(long pos)
        {
            var p = Fix(pos);
            if (p <= Length && cx.db.objects[p] is DBObject nb)
                return nb;
            if (p == pos)
                return (DBObject)cx.obs[p]?._Relocate(this);
            if (cx.obs[p] is DBObject x)
                return x;
            var ob = cx.obs[pos];
            if ((pos>=Transaction.TransPos && pos<Transaction.Executables)
                || pos>=Transaction.HeapStart)
            {
                ob = ob.Relocate(p).Relocate(this);
                p = ob.defpos;
                cx.obs -= pos;
                cx.obs += (p,ob);
                return ob;
            }
            return ob;
        }
        internal BList<long> Fix(BList<long> ord)
        {
            var r = BList<long>.Empty;
            var ch = false;
            for (var b = ord?.First(); b != null; b = b.Next())
            {
                var p = b.value();
                var f = Fix(p);
                if (p != f)
                    ch = true;
                r += f;
            }
            return ch ? r : ord;
        }
        internal CList<long> Fix(CList<long> ord)
        {
            var r = CList<long>.Empty;
            var ch = false;
            for (var b=ord?.First();b!=null;b=b.Next())
            {
                var p = b.value();
                var f = Fix(p);
                if (p != f)
                    ch = true;
                r += f;
            }
            return ch? r : ord;
        }
        internal CList<TypedValue> Fix(CList<TypedValue> ord)
        {
            var r = CList<TypedValue>.Empty;
            var ch = false;
            for (var b = ord?.First(); b != null; b = b.Next())
            {
                var p = b.value();
                var f = p.Relocate(this);
                if (p != f)
                    ch = true;
                r += f;
            }
            return ch ? r : ord;
        }
        internal CTree<TypedValue, long> Fix(CTree<TypedValue, long> mu)
        {
            var r = CTree<TypedValue, long>.Empty;
            for (var b = mu?.First(); b != null; b = b.Next())
                r += (b.key().Relocate(this),b.value());
            return r;
        }
        internal CTree<K,long> Fix<K>(CTree<K,long> us) where K:IComparable
        {
            var r = CTree<K,long>.Empty;
            var ch = false;
            for (var b = us?.First(); b != null; b = b.Next())
            {
                var p = b.value();
                var f = Fixed(p);
                if (p != f.defpos)
                    ch = true;
                r += (b.key(),f.defpos);
            }
            return ch ? r : us;
        }
        internal CTree<string, TypedValue> Fix(CTree<string, TypedValue> a)
        {
            var r = CTree<string, TypedValue>.Empty;
            for (var b = a?.First(); b != null; b = b.Next())
            {
                var p = b.key();
                r += (p, b.value().Relocate(this));
            }
            return r;
        }
        internal CTree<PTrigger.TrigType,CTree<long,bool>> Fix(CTree<PTrigger.TrigType,CTree<long,bool>> t)
        {
            var r = CTree<PTrigger.TrigType, CTree<long, bool>>.Empty;
            for (var b = t.First(); b != null; b = b.Next())
            {
                var p = b.key();
                r += (p, Fix(b.value()));
            }
            return r;
        }
        internal CTree<long, CList<TypedValue>> Fix(CTree<long, CList<TypedValue>> refs, Context nc)
        {
            var r = CTree<long, CList<TypedValue>>.Empty;
            var ch = false;
            for (var b = refs?.First(); b != null; b = b.Next())
            {
                var p = Fixed(b.key()).defpos;
                var vs = Fix(b.value());
                ch = ch || (p != b.key()) || vs != b.value();
                r += (p, vs);
            }
            return ch ? r : refs;
        }
        internal CList<K> Fix<K>(CList<K> key) where K:TypedValue
        {
            var r = CList<K>.Empty;
            var ch = false;
            for (var b = key?.First(); b != null; b = b.Next())
            {
                var p = b.value();
                var f =(K)p.Relocate(this);
                if (p != f)
                    ch = true;
                r += f;
            }
            return ch ? r : key;
        }
        internal BTree<long, SqlValue> Fix(BTree<long, SqlValue> rs)
        {
            var r = BTree<long, SqlValue>.Empty;
            var ch = false;
            for (var b = rs.First(); b != null; b = b.Next())
            {
                var p = Fix(b.key());
                var d = (SqlValue)b.value()._Relocate(this);
                ch = ch || p != b.key() || d != b.value();
                r += (p, d);
            }
            return ch ? r : rs;
        }
        internal BList<Grouping> Fix(BList<Grouping> gs)
        {
            var r = BList<Grouping>.Empty;
            var ch = false;
            for (var b = gs.First(); b != null; b = b.Next())
            {
                var g = (Grouping)b.value()._Relocate(this);
                ch = ch || g != b.value();
                r += g;
            }
            return ch ? r : gs;
        }
        internal BList<Domain> Fix(BList<Domain> ds)
        {
            var r = BList<Domain>.Empty;
            var ch = false;
            for (var b = ds?.First(); b != null; b = b.Next())
            {
                var d = b.value();
                var nd = (Domain)d._Relocate(this);
                if (d != nd)
                    ch = true;
                r += nd;
            }
            return ch ? r : ds;
        }
        internal BList<(SqlXmlValue.XmlName,long)> Fix(BList<(SqlXmlValue.XmlName, long)> cs)
        {
            var r = BList<(SqlXmlValue.XmlName, long)>.Empty;
            for (var b = cs.First(); b != null; b = b.Next())
            {
                var (n, p) = b.value();
                var np = Fixed(p).defpos;
                r += (n,np);
            }
            return r;
        }
        internal CTree<long,long> Fix(CTree<long,long> fd)
        {
            var r = CTree<long, long>.Empty;
            var ch = false;
            for (var b = fd?.First(); b != null; b = b.Next())
            {
                var p = b.key();
                var v = b.value();
                var np = Fix(p);
                var nv = Fix(v);
                if (p != np || v!=nv)
                    ch = true;
                r += (np, nv);
            }
            return ch ? r : fd;
        }
        internal BTree<long, long?> Fix(BTree<long, long?> fd)
        {
            var r = BTree<long, long?>.Empty;
            var ch = false;
            for (var b = fd?.First(); b != null; b = b.Next())
            {
                var p = b.key();
                var v = b.value();
                var np = Fix(p);
                var nv = Fix(v.Value);
                if (p != np || v != nv)
                    ch = true;
                r += (np, nv);
            }
            return ch ? r : fd;
        }
        internal CTree<long, V> Fix<V>(CTree<long, V> fi) where V:IComparable
        {
            var r = CTree<long, V>.Empty;
            var ch = false;
            for (var b = fi?.First(); b != null; b = b.Next())
            {
                var p = b.key();
                var np = Fix(p);
                if (p != np)
                    ch = true;
                r += (np, b.value());
            }
            return ch ? r : fi;
        }
        internal CTree<long, CTree<long, bool>> Fix(CTree<long, CTree<long, bool>> fi)
        {
            var r = CTree<long, CTree<long, bool>>.Empty;
            var ch = false;
            for (var b = fi?.First(); b != null; b = b.Next())
            {
                var p = b.key();
                var np = Fix(p);
                if (p != np)
                    ch = true;
                r += (np, Fix(b.value()));
            }
            return ch ? r : fi;
        }
        internal CTree<long,Domain> Fix(CTree<long,Domain> rs)
        {
            var r = CTree<long, Domain>.Empty;
            var ch = false;
            for (var b = rs.First(); b != null; b = b.Next())
            {
                var rk = b.key();
                var nk = Fix1(rk);
                var od = b.value();
                var rr = (Domain)od._Relocate(this);
                if (rr != b.value() || rk != nk)
                    ch = true;
                r += (nk, rr);
            }
            return ch ? r : rs;
        }
        internal CList<UpdateAssignment> Fix(CList<UpdateAssignment> us)
        {
            var r = CList<UpdateAssignment>.Empty;
            var ch = false;
            for (var b = us?.First(); b != null; b = b.Next())
            {
                var u = (UpdateAssignment)b.value()._Relocate(this);
                ch = ch || u != b.value();
                r += u;
            }
            return ch ? r : us;
        }
        internal CTree<UpdateAssignment,bool> Fix(CTree<UpdateAssignment,bool> us)
        {
            var r = CTree<UpdateAssignment,bool>.Empty;
            var ch = false;
            for (var b = us?.First(); b != null; b = b.Next())
            {
                var u = (UpdateAssignment)b.key()._Relocate(this);
                ch = ch || u != b.key();
                r += (u,b.value());
            }
            return ch ? r : us;
        }
        internal CTree<string,CTree<long,long>> Fix
            (CTree<string, CTree<long, long>> vc)
        {
            var r = CTree<string, CTree<long, long>>.Empty;
            for (var b = vc.First(); b != null; b = b.Next())
                r += (b.key(), Fix(b.value()));
            return r;
        }
        internal CTree<long, TypedValue> Fix(CTree<long, TypedValue> fi)
        {
            var r = CTree<long, TypedValue>.Empty;
            var ch = false;
            for (var b = fi?.First(); b != null; b = b.Next())
            {
                var p = b.key();
                var np = Fix(p);
                if (p != np)
                    ch = true;
                r += (np, b.value());
            }
            return ch ? r : fi;
        }
        internal PRow Fix(PRow rw)
        {
            if (rw == null)
                return null;
            return new PRow(rw._head?.Relocate(this), Fix(rw._tail));
        }
        internal CTree<long, RowSet.Finder> Fix(CTree<long, RowSet.Finder> fi)
        {
            var r = CTree<long, RowSet.Finder>.Empty;
            for (var b = fi.First(); b != null; b = b.Next())
                r += (Fix(b.key()), b.value().Relocate(this));
            return r;
        }
        internal BTree<SqlValue, TypedValue> Fix(BTree<SqlValue, TypedValue> vt)
        {
            var r = BTree<SqlValue, TypedValue>.Empty;
            var ch = false;
            for (var b = vt?.First(); b != null; b = b.Next())
            {
                var p = (SqlValue)b.key()._Relocate(this);
                var v = b.value().Relocate(this);
                if (p != b.key() || v != b.value())
                    ch = true;
                r += (p, b.value());
            }
            return ch ? r : vt;
        }
    }

    public class ReaderBase : IOBase
    {
        internal Database database;
        FileStream file;
        public long limit; 
        internal BTree<long, Physical.Type> log;
        public bool locked = false;
        internal ReaderBase(Database db, long p)
        {
            database = db;
            file = db._File();
            log = db.log;
            limit = file.Length;
            GetBuf(p);
        }
        public override int GetBuf(long s)
        {
            int m = (limit == 0 || limit >= s + Buffer.Size) ? Buffer.Size : (int)(limit - s);
            bool taken = false;
            try
            {
                if (!locked)
                    Monitor.Enter(file, ref taken);
                file.Seek(s, SeekOrigin.Begin);
                buf.len = file.Read(buf.buf, 0, m);
                buf.pos = 0;
            }
            finally
            {
                if (taken)
                {
                    Monitor.Exit(file);
                    locked = false;
                }
            }
            buf.start = s;
            return buf.len;
        }
        public override int ReadByte()
        {
            if (Position >= limit)
                return -1;
            if (buf.pos == buf.len)
            {
                int n = GetBuf(buf.start + buf.len);
                if (n < 0)
                    return -1;
                buf.pos = 0;
            }
            return buf.buf[buf.pos++];
        }
        public virtual long Position => buf.start + buf.pos;
        internal virtual void Set(Physical ph) 
        {
        }
        internal virtual void Segment(Physical ph) 
        {
            ph.trans = GetLong();
        }
        internal virtual DBObject GetObject(long pp)
        {
            return null;
        }
        internal virtual void Upd(PColumn3 pc) { }
        internal virtual long? Prev(long pv)
        {
            if (pv < 0)
                return null;
            return GetPhysical(pv).Affects;
        }
        internal virtual void Setup(PDomain pd)
        {
            TypedValue dv = TNull.Value;
            var ds = pd.domain.defaultString;
            var domain = pd.domain;
            if (ds.Length > 0
                && pd.domain.kind == Sqlx.CHAR && ds[0] != '\'')
                ds = "'" + ds + "'";
            if (ds != "")
                try
                {
                    dv = Domain.For(domain.kind).Parse(database.uid, ds);
                }
                catch (Exception) { }
            domain += (Domain.Default, dv);
            if (pd.eldefpos >= 0)
            {
                if (domain.kind == Sqlx.ARRAY || domain.kind == Sqlx.MULTISET
                    || domain.kind == Sqlx.SENSITIVE)
                    domain += (Domain.Element, GetDomain(pd.eldefpos,pd.ppos));
                else
                    domain = new UDType(pd.ppos,pd.domain);
            }
            pd.domain = domain;
        }
        internal virtual void Setup(Modify pd)
        { }
        internal virtual void Setup(Ordering od)
        { }
        /// <summary>
        /// Get the Domain for a given TableColumn defpos
        /// </summary>
        /// <param name="log"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        internal (string,Domain) GetColumnDomain(long cp,long p)
        {
            var tp = -1L;
            var n = "";
            for (var cb = database.colTracker[cp]?.First(); cb != null; cb = cb.Next())
            {
                tp = cb.key();
                if (tp > p)
                    break;
                n = cb.value();
            }
            return (n,GetDomain(tp,p));
        }
        internal Domain GetDomain(long tp,long p)
        {
            var r = Domain.Null;
            for (var b = database.typeTracker[tp]?.First();b!=null;b=b.Next())
            {
                var dp = b.key();
                if (dp > p)
                    break;
                r = b.value();
            }
            return r;
        }
        /// <summary>
        /// Get the Domain for a given table defpos and column name
        /// </summary>
        /// <param name="log"></param>
        /// <param name="tb"></param>
        /// <param name="cn"></param>
        /// <returns></returns>
        internal virtual (long, Domain) GetDomain(long tb, string cn, long pp)
        {
            var dm = GetDomain(tb, pp);
            for (var b=dm.rowType.First();b!=null;b=b.Next())
            {
                var cp = b.value();
                var (n, cdt) = GetColumnDomain(cp, pp);
                if (n == cn)
                    return (cp, cdt);
            }
            return (-1L, Domain.Content);
        }
        internal Physical GetPhysical(long pv)
        {
            if (pv < 0)
                return null;
            return new ReaderBase(database, pv).Create();
        }
        internal Integer GetInteger()
        {
            var n = ReadByte();
            var cs = new byte[n];
            for (int j = 0; j < n; j++)
                cs[j] = (byte)ReadByte();
            return new Integer(cs);
        }
        public int GetInt()
        {
            return GetInteger();
        }
        internal int GetInt32()
        {
            var r = 0;
            for (var i = 0; i < 4; i++)
                r = (r << 8) + ReadByte();
            return r;
        }
        public long GetLong()
        {
            return GetInteger();
        }
        public string GetString()
        {
            int n = GetInt();
            byte[] cs = new byte[n];
            for (int j = 0; j < n; j++)
                cs[j] = (byte)ReadByte();
            return Encoding.UTF8.GetString(cs, 0, n);
        }
        internal Ident GetIdent()
        {
            var p = Position;
            var s = GetString();
            return (s == "") ? null : new Ident(s, p);
        }
        /// <summary>
        /// Get a Numeric from the buffer
        /// </summary>
        /// <returns>a new Numeric</returns>
        internal Common.Numeric GetDecimal()
        {
            Integer m = GetInteger();
            return new Common.Numeric(m, GetInt());
        }
        /// <summary>
        /// Get a Real from the buffer
        /// </summary>
        /// <returns>a new real</returns>
        public double GetDouble()
        {
            return GetDecimal();
        }
        /// <summary>
        /// Get a dateTime from the buffer
        /// </summary>
        /// <returns>a new datetime</returns>
        public DateTime GetDateTime()
        {
            return new DateTime(GetLong());
        }
        /// <summary>
        /// Get an Interval from the buffer
        /// </summary>
        /// <returns>a new Interval</returns>
        internal Interval GetInterval()
        {
            var ym = (byte)ReadByte();
            if (ym == 1)
            {
                var years = GetInt();
                var months = GetInt();
                return new Interval(years, months);
            }
            else
                return new Interval(GetLong());
        }
        /// <summary>
        /// Attempt some backward compatibility
        /// </summary>
        /// <returns></returns>
        internal Interval GetInterval0()
        {
            var years = GetInt();
            var months = GetInt();
            var ticks = GetLong();
            var r = new Interval(years, months, ticks);
            return r;
        }
        /// <summary>
        /// Get an array of bytes from the buffer
        /// </summary>
        /// <returns>the new byte array</returns>
        public byte[] GetBytes()
        {
            int n = GetInt();
            byte[] b = new byte[n];
            for (int j = 0; j < n; j++)
                b[j] = (byte)ReadByte();
            return b;
        }
        protected bool EoF()
        {
            return Position >= limit;
        }
        internal Physical Create()
        {
            if (EoF())
                return null;
            Physical.Type tp = (Physical.Type)ReadByte();
            Physical p;
            switch (tp)
            {
                default: throw new PEException("PE35");
                case Physical.Type.Alter: p = new Alter(this); break;
                case Physical.Type.Alter2: p = new Alter2(this); break;
                case Physical.Type.Alter3: p = new Alter3(this); break;
                case Physical.Type.AlterRowIri: p = new AlterRowIri(this); break;
                case Physical.Type.Change: p = new Change(this); break;
                case Physical.Type.Checkpoint: p = new Checkpoint(this); break;
                case Physical.Type.Curated: p = new Curated(this); break;
                case Physical.Type.Delete: p = new Delete(this); break;
                case Physical.Type.Drop: p = new Drop(this); break;
                case Physical.Type.Edit: p = new Edit(this); break;
                case Physical.Type.EndOfFile:
                    p = new EndOfFile(this); break;
                case Physical.Type.Grant: p = new Grant(this); break;
                case Physical.Type.Metadata: p = new PMetadata(this); break;
                case Physical.Type.Modify: p = new Modify(this); break;
                case Physical.Type.Namespace: p = new Namespace(this); break;
                case Physical.Type.Ordering: p = new Ordering(this); break;
                case Physical.Type.PCheck: p = new PCheck(this); break;
                case Physical.Type.PCheck2: p = new PCheck2(this); break;
                case Physical.Type.PColumn: p = new PColumn(this); break;
                case Physical.Type.PColumn2: p = new PColumn2(this); break;
                case Physical.Type.PColumn3: p = new PColumn3(this); break;
                case Physical.Type.PDateType: p = new PDateType(this); break;
                case Physical.Type.PDomain: p = new PDomain(this); break;
                case Physical.Type.PDomain1: p = new PDomain1(this); break;
                case Physical.Type.PeriodDef: p = new PPeriodDef(this); break;
                case Physical.Type.PImportTransaction: p = new PImportTransaction(this); break;
                case Physical.Type.PIndex: p = new PIndex(this); break;
                case Physical.Type.PIndex1: p = new PIndex1(this); break;
                case Physical.Type.PMethod: p = new PMethod(tp, this); break;
                case Physical.Type.PMethod2: p = new PMethod(tp, this); break;
                case Physical.Type.PProcedure: p = new PProcedure(tp, this); break;
                case Physical.Type.PProcedure2: p = new PProcedure(tp, this); break;
                case Physical.Type.PRole: p = new PRole(this); break;
                case Physical.Type.PRole1: p = new PRole(this); break;
                case Physical.Type.PTable: p = new PTable(this); break;
                case Physical.Type.PTable1: p = new PTable1(this); break;
                case Physical.Type.PTransaction: p = new PTransaction(this); break;
                //         case Physical.Type.PTransaction2: p = new PTransaction2(this); break;
                case Physical.Type.PTrigger: p = new PTrigger(this); break;
                case Physical.Type.PType: p = new PType(this); break;
                case Physical.Type.PType1: p = new PType1(this); break;
                case Physical.Type.PUser: p = new PUser(this); break;
                case Physical.Type.PView: p = new PView(this); break;
                case Physical.Type.PView1: p = new PView1(this); break; //obsolete
                case Physical.Type.RestView: p = new PRestView(this); break;
                case Physical.Type.RestView1: p = new PRestView1(this); break;
                case Physical.Type.Record: p = new Record(this); break;
                case Physical.Type.Record1: p = new Record1(this); break;
                case Physical.Type.Record2: p = new Record2(this); break;
                //          case Physical.Type.Reference: p = new Reference(this); break;
                case Physical.Type.Revoke: p = new Revoke(this); break;
                case Physical.Type.Update: p = new Update(this); break;
                case Physical.Type.Versioning: p = new Versioning(this); break;
                //          case Physical.Type.Reference1: p = new Reference1(this); break;
                case Physical.Type.ColumnPath: p = new PColumnPath(this); break;
                case Physical.Type.Metadata2: p = new PMetadata2(this); break;
                case Physical.Type.PIndex2: p = new PIndex2(this); break;
                //          case Physical.Type.DeleteReference1: p = new DeleteReference1(this); break;
                case Physical.Type.Authenticate: p = new Authenticate(this); break;
                case Physical.Type.TriggeredAction: p = new TriggeredAction(this); break;
                case Physical.Type.Metadata3: p = new PMetadata3(this); break;
                case Physical.Type.RestView2: p = new PRestView2(this); break;
                case Physical.Type.Audit: p = new Audit(this); break;
                case Physical.Type.Classify: p = new Classify(this); break;
                case Physical.Type.Clearance: p = new Clearance(this); break;
                case Physical.Type.Enforcement: p = new Enforcement(this); break;
                case Physical.Type.Record3: p = new Record3(this); break;
                case Physical.Type.Update1: p = new Update1(this); break;
                case Physical.Type.Delete1: p = new Delete1(this); break;
                case Physical.Type.Drop1: p = new Drop1(this); break;
                case Physical.Type.RefAction: p = new RefAction(this); break;
            }
            p.Deserialise(this);
            return p;
        }
    }
    public class Reader : ReaderBase
    {
        internal Context context;
        internal Role role;
        internal User user;
        internal PTransaction trans = null;
        public long segment;
        public override int ReadByte()
        {
            if (Position >= limit)
                return -1;
            if (buf.pos == buf.len)
            {
                int n = GetBuf(buf.start + buf.len);
                if (n < 0)
                    return -1;
                buf.pos = 0;
            }
            return buf.buf[buf.pos++];
        }
        internal Reader(Context cx) : base(cx.db,cx.db.loadpos)
        {
            var db = cx.db;
            context = new Context(db);
            role = db.role;
            user = (User)db.objects[db.owner];
        }
        internal Reader(Context cx, long p, PTransaction pt=null)
            :base(cx.db,p)
        {
            var db = cx.db;
            context = new Context(db);
            role = db.role;
            user = (User)db.objects[db.owner];
            trans = pt;
        }
        internal override void Set(Physical ph)
        {
            ph.trans = trans?.ppos ?? 0;
            ph.time = trans?.pttime ?? 0;
        }
        internal override void Segment(Physical ph)
        {
            segment = GetLong();
            ph.trans = segment;
        }
        internal override DBObject GetObject(long pp)
        {
            return (DBObject)context.db.objects[pp];
        }
        internal override void Upd(PColumn3 pc)
        {
            if (pc.ups != "")
                try
                {
                    pc.upd = new Parser(context).ParseAssignments(pc.ups, pc.table.domain);
                }
                catch (Exception)
                {
                    pc.upd = CTree<UpdateAssignment, bool>.Empty;
                }
        }
        internal override long? Prev(long pv)
        {
            return (long?)context.db.objects[pv];
        }
        internal override void Setup(PDomain pd)
        {
            TypedValue dv = TNull.Value;
            var ds = pd.domain.defaultString;
            var domain = pd.domain;
            if (ds.Length > 0
                && pd.domain.kind == Sqlx.CHAR && ds[0] != '\'')
                ds = "'" + ds + "'";
            if (ds != "")
                try
                {
                    dv = Domain.For(domain.kind).Parse(context.db.uid, ds);
                    domain += (Domain.Default, dv);
                }
                catch (Exception) { }
            if (pd.eldefpos >= 0)
            {
                if (domain.kind == Sqlx.ARRAY || domain.kind == Sqlx.MULTISET 
                    || domain.kind == Sqlx.SENSITIVE)
                    domain += (Domain.Element, context.db.objects[pd.eldefpos]);
                else
                {
                    var tb = (Table)context.db.objects[pd.eldefpos];
                    var rs = CTree<long, Domain>.Empty;
                    for (var b = tb.domain.rowType.First(); b != null; b = b.Next())
                    {
                        var tc = (DBObject)context.db.objects[b.value()];
                        rs += (b.value(), tc.domain);
                    }
                    domain = domain + (Domain.Structure, pd.eldefpos)
                        + (Domain.RowType, tb.domain.rowType)
                        + (Domain.Representation, rs);
                }
            }
            if (pd is PType) // the structure may have just been defined
            {
                for (var b=context.db.objects.Last();b!=null;b=b.Previous())
                    if (b.value() is Table st)
                    {
                        if (st.name is string n && n.Length>0 && n[0]=='(')
                            domain = domain + (Domain.Structure, st.defpos)
                                + (Domain.RowType, st.domain.rowType)
                                + (Domain.Representation, st.domain.representation);
                        break;
                    }
            }
            pd.domain = domain;
            context.db += (pd.domdefpos, pd.domain, Position);
        }
        internal override void Setup(Ordering od)
        {
            od.domain = (Domain)context.db.objects[od.domdefpos];
        }
        internal override void Setup(Modify pm)
        {
            switch (pm.name)
            {
                default:
                    {
                        var mi = (ObInfo)context.role.infos[pm.modifydefpos];
                        var mt = (Method)context.db.objects[pm.modifydefpos];
                        if (mi.domain is UDType udt)
                        {
                            var psr = new Parser(context, new Ident(pm.body, pm.ppos + 2));
                            var (pps, xp) = psr.ParseProcedureHeading(new Ident(pm.name, pm.ppos + 1));
                            for (var b = udt.representation.First(); b != null; b = b.Next())
                            {
                                var p = b.key();
                                var ic = new Ident(psr.cx.Inf(p).name, p);
                                psr.cx.defs += (ic, p);
                                psr.cx.Add(new SqlValue(ic) + (DBObject._Domain, b.value()));
                            }
                            pm.now = psr.ParseProcedureStatement(xp,null,null);
                            pm.framing = new Framing(psr.cx);
                            context.db = psr.cx.db;
                            pm.Frame(psr.cx);
                            pm.framing += (Framing.Obs,
                                pm.framing.obs + (mt.defpos, mt + (Procedure.Body, pm.now.defpos)));
                            pm.bodydefpos = pm.now.defpos;
                            pm.parms = pps;
                        }
                        break;
                    }
                case "Source":
                    {
                        var ps = context.db.objects[pm.modifydefpos] as Procedure;
                        pm.now = new Parser(context).ParseQueryExpression(new Ident(pm.body,pm. ppos + 1), ps.domain);
                        break;
                    }
                case "Insert": // we ignore all of these (PView1)
                case "Update":
                case "Delete":
                    pm.now = null;
                    break;
            }
        }
        internal void Add(Physical ph)
        {
            ph.OnLoad(this);
            context.result = -1L;
            context.frameFix = Position-1;
            context.db.Add(context, ph, Position);
        }
        /// <summary>
        /// This important routine is part of the Commit sequence. It looks for
        /// physicals committed by concurrent transactions. Because transactions can be
        /// very long, we call this routine twice. The first time, the we do not lock
        /// the file, so we must accept that we may need to give up partway through the
        /// the last complete Physical (this is not a problem).
        /// The second time GetAll is called, the file will already be locked and we want to 
        /// restart from the last Physical boundary.This time if the record is incomplete
        /// we throw an exception.
        /// </summary>
        /// <returns>The list of concurrent physicals</returns>
        internal BList<Physical> GetAll()
        {
            var r = BList<Physical>.Empty;
            try { 
                for (long p = Position; p < limit; p = Position) // will have moved on
                    r += Create();
            } catch(Exception)
            {
                if (locked)
                    throw new Exception("GetAll "+Position);
            }
            return r;
        }
    }


}

