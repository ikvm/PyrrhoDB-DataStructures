using System;
using System.Collections.Generic;
using Pyrrho.Common;
using Pyrrho.Level4;
using System.Text;
using Pyrrho.Level3;

// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2019
//
// This software is without support and no liability for damage consequential to use
// You can view and test this code 
// All other use or distribution or the construction of any product incorporating this technology 
// requires a license from the University of the West of Scotland
namespace Pyrrho.Level2
{
    /// <summary>
    /// PIndex is a way of associating a BTree with a PTable
    /// A PIndex is made whenever there is a primary, references or unique constraint
    /// </summary>
    internal class PIndex : Physical
    {
        /// <summary>
        /// The defining position of the index
        /// </summary>
        public virtual long defpos { get { return ppos; } }
        /// <summary>
        /// The name of the index: generated as U(nnn)
        /// </summary>
        public string name;
        /// <summary>
        /// The hosting table
        /// </summary>
        public long tabledefpos;
        /// <summary>
        /// The key TableColumns for the index 
        /// </summary>
        public BList<long> columns;
        /// <summary>
        /// The constraint type
        /// </summary>
        public ConstraintType flags = 0;
        public ulong metadata = 0UL;
        /// <summary>
        /// Constraint Type for the Index.
        /// Desc does not seem to be used. 
        /// These values are written to the database and should not be changed
        /// </summary>
        [Flags]
        public enum ConstraintType
        {
            NoType = 0, PrimaryKey = 1, ForeignKey = 2, Unique = 4, Desc = 8,
            ConstrainUpdate = 16, CascadeUpdate = 32, SetDefaultUpdate = 64, SetNullUpdate = 128,
            ConstrainDelete = 256, CascadeDelete = 512, SetDefaultDelete = 1024, SetNullDelete = 2048,
            SystemTimeIndex = 4096, ApplicationTimeIndex = 8192
        }
        /// <summary>
        /// The referenced Index for a foreign key
        /// </summary>
        public long reference;
       /// <summary>
        /// The adapter function (PIndex1)
        /// </summary>
        public string adapter = "";
        public override long Dependent(Writer wr)
        {
            if (defpos!=ppos && !Committed(wr,defpos)) return defpos;
            if (!Committed(wr,tabledefpos)) return tabledefpos;
            for (var b=columns.First();b!=null;b=b.Next())
                if (!Committed(wr,b.value())) return b.value();
            if (!Committed(wr,reference)) return reference;
            return -1;
        }
        /// <summary>
        /// Constructor: A new PIndex request from the Parser
        /// </summary>
        /// <param name="nm">The name of the index</param>
        /// <param name="tb">The defining position of the table being indexed</param>
        /// <param name="cl">The defining positions of key TableColumns</param>
        /// <param name="fl">The constraint flags</param>
        /// <param name="rx">The defining position of the referenced index (or -1)</param>
        /// <param name="tb">The physical database</param>
        /// <param name="curpos">The current position in the datafile</param>
        public PIndex(string nm, long tb, BList<Selector> cl,
            ConstraintType fl, long rx, long u, Transaction tr) :
            this(Physical.Type.PIndex, nm, tb, cl, fl, rx, u, tr)
        { }
        /// <summary>
        /// Constructor: A new PIndex request from the Parser
        /// </summary>
        /// <param name="t">The PIndex type</param>
        /// <param name="nm">The name of the index</param>
        /// <param name="tb">The defining position of the table being indexed</param>
        /// <param name="cl">The defining positions of key TableColumns</param>
        /// <param name="fl">The constraint flags</param>
        /// <param name="rx">The defining position of the referenced index (or -1)</param>
        /// <param name="tb">The physical database</param>
        /// <param name="curpos">The current position in the datafile</param>
        public PIndex(Type t, string nm, long tb, BList<Selector> cl,
            ConstraintType fl, long rx, long u,Transaction tr) :
            base(t, u, tr)
        {
            name = nm?? throw new DBException("42102").Mix();
            tabledefpos = tb;
            var c = BList<long>.Empty;
            for (var b = cl.First(); b != null; b = b.Next())
                c += (b.key(), b.value().defpos);
            columns = c;
            flags = fl;
            reference = rx;
        }
        /// <summary>
        /// Constructor: A new PIndex request from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">Position in the buffer</param>
        public PIndex(Reader rdr) : base(Type.PIndex, rdr) { }
        /// <summary>
        /// Constructor: A new PIndex request from the buffer
        /// </summary>
        /// <param name="t">The PIndex type</param>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">Position in the buffer</param>
        public PIndex(Type t, Reader rdr) : base(t, rdr) { }
        protected PIndex(PIndex x, Writer wr) : base(x, wr)
        {
            name = x.name;
            tabledefpos = wr.Fix(x.tabledefpos);
            columns = BList<long>.Empty;
            for (var b = x.columns.First(); b != null; b = b.Next())
                columns += (b.key(), wr.Fix(b.value()));
            flags = x.flags;
            reference = wr.Fix(x.reference);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PIndex(this, wr);
        }
        /// <summary>
        /// The Affedcted Physical is this
        /// </summary>
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocatioon information for positions</param>
        public override void Serialise(Writer wr) //LOCKED
        {
            tabledefpos = wr.Fix(tabledefpos);
            wr.PutString(name.ToString());
            wr.PutLong(tabledefpos);
            wr.PutInt((int)columns.Count);
            for (int j = 0; j < columns.Count; j++)
                wr.PutLong(columns[j]);
            wr.PutInt((int)flags);
            reference = wr.Fix(reference);
            wr.PutLong(reference);
            base.Serialise(wr);
        }
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            name = rdr.GetString();
            tabledefpos = rdr.GetLong();
            int n = rdr.GetInt();
            if (n > 0)
            {
                columns = BList<long>.Empty;
                for (int j = 0; j < n; j++)
                    columns+=(j,rdr.GetLong());
            }
            flags = (ConstraintType)rdr.GetInt();
            reference = rdr.GetLong();
            base.Deserialise(rdr);
        }
        public override long Conflicts(Database db, Transaction tr, Physical that)
        {
            switch(that.type)
            {
                case Type.Alter3:
                    return (((Alter3)that).tabledefpos == tabledefpos) ? ppos : -1;
                case Type.Alter2:
                    return (((Alter2)that).tabledefpos == tabledefpos) ? ppos : -1;
                case Type.Alter:
                    return (((Alter)that).tabledefpos == tabledefpos) ? ppos : -1;
                case Type.PIndex2:
                case Type.PIndex1:
                case Type.PIndex:
                    return (((PIndex)that).tabledefpos == tabledefpos) ? ppos : -1;
                case Type.Drop:
                    {
                        var d = (Drop)that;
                        if (d.delpos == tabledefpos || d.delpos == reference)
                            return ppos;
                        for (int j = 0; j < columns.Count; j++)
                            if (d.delpos == columns[j] || d.delpos == -columns[j])
                                return ppos;
                        break;
                    }
            }
            return base.Conflicts(db, tr, that);
        }
        /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>The string representation</returns>
        public override string ToString()
        {
            string r = "PIndex " + name;
            r = r + " on " + Pos(tabledefpos) + "(";
            for (int j = 0; j < columns.Count; j++)
                r += ((j > 0) ? "," : "") + DBObject.Uid(columns[j]);
            r += ") " + flags.ToString();
            if (reference >= 0)
                r += " refers to [" + Pos(reference) + "]";
            return r;
        }

        internal override Database Install(Database db, Role ro, long p)
        {
            var x = new Index(this, db).Build(db);
            var tb = (Table)db.schemaRole.objects[tabledefpos];
            tb += (Table.Indexes,tb.indexes+(x.defpos,x));
            return db + (db.role,tb);
        }
    }
    /// <summary>
    /// PIndex1 is used for conditional or adapted referential constraints
    /// </summary>
    internal class PIndex1 : PIndex
    {
        /// <summary>
        /// Constructor: A new PIndex request from the Parser
        /// </summary>
        /// <param name="nm">The name of the index</param>
        /// <param name="tb">The defining position of the table being indexed</param>
        /// <param name="cl">The defining positions of key TableColumns</param>
        /// <param name="fl">The constraint flags</param>
        /// <param name="rx">The defining position of the referenced index (or -1)</param>
        /// <param name="af">The adapter function</param>
        /// <param name="tb">The physical database</param>
        /// <param name="curpos">The current position in the datafile</param>
        public PIndex1(string nm, long tb, BList<Selector> cl,
            ConstraintType fl, long rx, string af, long u,Transaction tr) :
            this(Type.PIndex1, nm, tb, cl, fl, rx, af, u,tr)
        { }
        /// <summary>
        /// Constructor: A new PIndex request from the Parser
        /// </summary>
        /// <param name="t">The PIndex type</param>
        /// <param name="nm">The name of the index</param>
        /// <param name="tb">The defining position of the table being indexed</param>
        /// <param name="cl">The defining positions of key TableColumns</param>
        /// <param name="fl">The constraint flags</param>
        /// <param name="rx">The defining position of the referenced index (or -1)</param>
        /// <param name="af">The adapter function</param>
        /// <param name="tb">The physical database</param>
        /// <param name="curpos">The current position in the datafile</param>
        public PIndex1(Type t, string nm, long tb, BList<Selector> cl,
            ConstraintType fl, long rx, string af, long u,Transaction tr) :
            base(t, nm, tb, cl, fl, rx, u,tr)
        {
            adapter = af;
        }
        /// <summary>
        /// Constructor: A new PIndex request from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">Position in the buffer</param>
        public PIndex1(Reader rdr) : base(Type.PIndex1, rdr) { }
        protected PIndex1(Type t, Reader rdr) : base(t, rdr) { }
        protected PIndex1(PIndex1 x, Writer wr) : base(x, wr)
        {
            adapter = x.adapter;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PIndex1(this, wr);
        }
        public override void Serialise(Writer wr) //LOCKED
        {
            wr.PutString(adapter);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            adapter = rdr.GetString();
            base.Deserialise(rdr);
        }
        public override string ToString()
        {
            return base.ToString() + " USING "+adapter;
        }
    }
    /// <summary>
    /// PIndex1 is used for adding metadata flags to an integrity or referential constraint
    /// </summary>
    internal class PIndex2 : PIndex1
    {
        /// <summary>
        /// Constructor: A new PIndex request from the Parser
        /// </summary>
        /// <param name="nm">The name of the index</param>
        /// <param name="tb">The defining position of the table being indexed</param>
        /// <param name="cl">The defining positions of key TableColumns</param>
        /// <param name="fl">The constraint flags</param>
        /// <param name="rx">The defining position of the referenced index (or -1)</param>
        /// <param name="af">The adapter function</param>
        /// <param name="md">The metadata flags</param>
        /// <param name="db">The database</param>
        public PIndex2(string nm, long tb, BList<Selector> cl,
            ConstraintType fl, long rx, string af, ulong md, long u,Transaction tr) :
            base(Type.PIndex2, nm, tb, cl, fl, rx, af, u, tr)
        {
            metadata = md;
        }
        /// <summary>
        /// Constructor: A new PIndex request from the Parser
        /// </summary>
        /// <param name="t">The PIndex type</param>
        /// <param name="nm">The name of the index</param>
        /// <param name="tb">The defining position of the table being indexed</param>
        /// <param name="cl">The defining positions of key TableColumns</param>
        /// <param name="fl">The constraint flags</param>
        /// <param name="rx">The defining position of the referenced index (or -1)</param>
        /// <param name="af">The adapter function</param>
        /// <param name="db">The physical database</param>
        public PIndex2(Type t, string nm, long tb, BList<Selector> cl,
            ConstraintType fl, long rx, string af, ulong md, long u, Transaction tr) :
            base(t, nm, tb, cl, fl, rx, af, u, tr)
        {
            metadata = md;
        }
        /// <summary>
        /// Constructor: A new PIndex request from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">Position in the buffer</param>
        public PIndex2(Reader rdr) : base(Type.PIndex2, rdr) { }
        protected PIndex2(PIndex2 x, Writer wr) : base(x, wr)
        {
            metadata = x.metadata;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PIndex2(this, wr);
        }
        public override void Serialise(Writer wr) //LOCKED
        {
            wr.PutLong((long)metadata);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            metadata = (ulong)rdr.GetLong();
            base.Deserialise(rdr);
        }
    }
}