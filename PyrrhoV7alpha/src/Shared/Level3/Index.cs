using Pyrrho.Level2;
using Pyrrho.Level4; // for rename/drop
using Pyrrho.Common;
using System.Text;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2023
//
// This software is without support and no liability for damage consequential to use.
// You can view and test this code, and use it subject for any purpose.
// You may incorporate any part of this code in other software if its origin 
// and authorship is suitably acknowledged.
// All other use or distribution or the construction of any product incorporating 
// this technology requires a license from the University of the West of Scotland.

namespace Pyrrho.Level3
{
    /// <summary>
    /// This class corresponds to logical Index database objects.
    /// Indexes are database objects that are created by primary key and unique constraints.
    /// Indexes have unique names of form U(nnn), since they are not named in SQL.
    /// // shareable as of 26 April 2021
    /// </summary>
    internal class Index : DBObject 
    {
        static long _uniq = 0;
        internal const long
            Adapter = -157, // long Procedure 
            IndexConstraint = -158,// PIndex.ConstraintType
            Keys = -159, // Domain
            References = -160, // BTree<long,BList<TypedValue>> computed by adapter
            RefIndex = -161, // long for Table: Index
                      // for RowSets: IndexRowSet/FilterRowSet/OrderedRowSet
            RefTable = -162, // long Table
            TableDefPos = -163, // long Table
            Tree = -164; // MTree
        /// <summary>
        /// Unique identifier nnn in U(nnn)
        /// </summary>
        public readonly long _nindex = _uniq++;
        /// <summary>
        /// The defining position for the table in the database.
        /// alas: if we cache table or valueType here we miss alterations, new TableColumns etc
        /// </summary>
        public long tabledefpos => (long)(mem[TableDefPos]??-1L);
        /// <summary>
        /// The flags describe the type of index
        /// </summary>
        public PIndex.ConstraintType flags => (PIndex.ConstraintType)(mem[IndexConstraint] ?? 0);
        /// <summary>
        /// The indexed rows: note the strong types inside here will need to be updated if column names change
        /// </summary>
        public MTree? rows => (MTree?)mem[Tree];
        public Domain keys => (Domain?)mem[Keys] ?? Domain.Null;
        /// <summary>
        /// for Foreign key, the referenced index
        /// </summary>
        public long refindexdefpos => (long)(mem[RefIndex] ?? -1L);
        /// <summary>
        /// for Foreign key, the referenced table
        /// </summary>
        public long reftabledefpos => (long)(mem[RefTable] ?? -1L);
        /// <summary>
        /// The adapter function
        /// </summary>
        public long adapter => (long)(mem[Adapter]??-1L);
        /// <summary>
        /// The references as computed by the adapter function if any
        /// </summary>
        public CTree<long, CList<TypedValue>> references =>
            (CTree<long, CList<TypedValue>>?)mem[References]??CTree<long,CList<TypedValue>>.Empty;
        public Index(long dp, BTree<long, object> m) : base(dp, m) { }
        /// <summary>
        /// Constructor: a new Index 
        /// </summary>
        /// <param name="tb">The level 3 database</param>
        /// <param name="c">The level 2 index</param>
        public Index(PIndex c, Context cx)
            : base(c.ppos, c.defpos, _IndexProps(c, cx)
                 + (TableDefPos, c.tabledefpos) + (IndexConstraint, c.flags)
                  + (ObInfo.Name, c.name))
        { }
        static BTree<long, object> _IndexProps(PIndex c, Context cx)
        {
            var ro = cx.role;
            var r = new BTree<long, object>(Definer,ro.defpos);
            if (c.adapter != "")
            {
                r += (Adapter, c.adapter);
                r += (References, BTree<long, BList<TypedValue>>.Empty);
            }
            if (c.reference > 0 && cx.db.objects[c.reference] is Index rx)
            {
                r += (RefIndex, rx.defpos);
                r += (RefTable, rx.tabledefpos);
            }
            var rt = BList<long?>.Empty;
            var rs = CTree<long, Domain>.Empty;
            for (var b = c.columns.First(); b != null; b = b.Next())
                if (b.value() is long pos)
                {
                    if (pos == 0 && cx.db.objects[c.tabledefpos] is Table tb &&
                        cx.db.objects[tb.systemPS] is PeriodDef pd)
                        pos = pd.startCol;
                    rt += pos;
                    rs += (pos, cx._Dom(pos) ?? throw new PEException("PE50201"));
                }
            var kd = new Domain(-1L, cx, Sqlx.ROW, rs, rt, rt.Length);
            TreeBehaviour isfk = (c.reference >= 0 || c.flags == PIndex.ConstraintType.NoType) ?
                TreeBehaviour.Allow : TreeBehaviour.Disallow;
            r += (Keys, kd);
            var rows = new MTree(kd, isfk, 0);
            r += (Tree, rows);
            return r;
        }
        public static Index operator +(Index x, (long, object) v)
        {
            return (Index)x.New(x.mem + v);
        }
        public static Index operator +(Index x,(CList<TypedValue>,long) y)
        {
            var (k, v) = y;
            if (x.rows == null)
                throw new PEException("PE3070");
            return x + (Tree, x.rows + (k,0,v));
        }
        public static Index operator -(Index x, CList<TypedValue> k)
        {
            if (x.rows is not MTree mt)
                throw new PEException("PE3071");
            MTree? nm = mt - k;
            return (nm==null)? new(x.defpos,x.mem-Tree) : x + (Tree, nm);
        }
        public static Index operator -(Index x, (CList<TypedValue>,long) y)
        {
            var (k,v) = y;
            if (x.rows is not MTree mt)
                throw new PEException("PE3072");
            MTree? nm = mt - (k,0,v);
            return (nm==null)? new(x.defpos,x.mem-Tree): x + (Tree, nm);
        }
        internal CList<TypedValue>? MakeKey(CTree<long,TypedValue> vs)
        {
            var r = CList<TypedValue>.Empty;
            for (var b = keys.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p)
                {
                    if (vs[p] is not TypedValue v)
                        return null;
                    r += v;
                }
            return r;
        }
        internal CList<TypedValue>? MakeKey(CTree<long,TypedValue> vs,BTree<long,long?> sIMap)
        {
            var r = CList<TypedValue>.Empty;
            for (var b = keys.rowType.First(); b != null; b = b.Next())
            {
                if (b.value() is long p && sIMap[p] is long q && vs[q] is TypedValue v)
                    r += v;
                else
                    return null;
            }
            return r;
        }
        /// <summary>
        /// Creator: an Index by modifying this from tableRows/versionedRows information
        /// </summary>
        /// <param name="db">The database</param>
        /// <returns>the new Index</returns>
        public Index Build(Context cx)
        {
            var rs = rows ?? throw new PEException("PE48128");
            if (cx.db.objects[tabledefpos] is not Table tb)
                throw new PEException("PE47146");
            bool rx = ((flags & PIndex.ConstraintType.ForeignKey) == PIndex.ConstraintType.ForeignKey);
            bool ux = ((flags & (PIndex.ConstraintType.PrimaryKey | PIndex.ConstraintType.Unique)) != PIndex.ConstraintType.NoType);
            if (tb.FindPrimaryIndex(cx) is Index px)
            {
                for (var d = px.rows?.First(); d != null; d = d.Next())
                    if (d.Value() is long pp && tb.tableRows[pp] is TableRow r)
                    {
                        var m = r.MakeKey(px);
                        if (m != null)
                        {
                            if (rx)
                                CheckRef(cx.db, m);
                            if (ux && rows.Contains(m) && tb.infos[cx.role.defpos] is ObInfo oi && oi.name != null)
                                throw new DBException("44002", "PRIMARY/UNIQUE", oi.name).Mix()
                                    .Add(Sqlx.TABLE_NAME, new TChar(oi.name))
                                    .Add(Sqlx.CONSTRAINT_NAME, new TChar("PRIMARY/UNIQUE"));
                            rs += (m, 0, pp);
                        }
                    }
            }
            else
            {
                // there is no primary index, so we do it from the tableRows information
                for (var pq = tb.tableRows.PositionAt(0); pq != null; pq = pq.Next())
                {
                    var rq = pq.value();
                    var m = rq.MakeKey(this);
                    if (m != null)
                    {
                        if (rx)
                            CheckRef(cx.db, m);
                        if (ux && rs.Contains(m))
                        {
                            var oi = tb.infos[cx.role.defpos];
                            throw new DBException("44002", "PRIMARY/UNIQUE").Mix()
                                  .Add(Sqlx.TABLE_NAME, new TChar(oi?.name ?? "??"))
                                  .Add(Sqlx.CONSTRAINT_NAME, new TChar("PRIMARY/UNIQUE"));
                        }
                        rs += (m, 0, pq.key());
                    }
                }
            }
            return this + (Tree, rs);
        }
        /// <summary>
        /// Check referential integrity
        /// </summary>
        /// <param name="m">The key to check</param>
        void CheckRef(Database db, CList<TypedValue> m)
        {
            if (db is Transaction && db.role != null && db.objects[refindexdefpos] is Index rx &&
           /*           if (rx is VirtualIndex)
                          return; */
           db.objects[reftabledefpos] is Table tb && (rx.rows == null || !rx.rows.Contains(m))
             && tb.infos[db.role.defpos] is ObInfo oi && oi.name!=null)
                throw new DBException("44002", "REFERENCES", oi.name).Mix()
                    .Add(Sqlx.TABLE_NAME, new TChar(oi.name))
                    .Add(Sqlx.CONSTRAINT_NAME, new TChar("REFERENCES"));
        }
        internal override void Cascade(Context cx,
            Drop.DropAction a = Level2.Drop.DropAction.Restrict, BTree<long, TypedValue>? u = null)
        {
            base.Cascade(cx, a, u);
  //          if (reftabledefpos >= 0 && cx.db != null && cx.db.objects[reftabledefpos] is Table ta)
  //              ta.FindPrimaryIndex(cx)?.Cascade(cx, a, u);
            for (var b = cx.role.dbobjects.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.db?.objects[p] is Table tb)
                    for (var xb = tb.indexes.First(); xb != null; xb = xb.Next())
                        for (var c = xb.value().First(); c != null; c = c.Next())
                            if (cx.db.objects[c.key()] is Index rx && rx.refindexdefpos == defpos)
                                rx.Cascade(cx, a, u);
        }
        internal override Database Drop(Database db, Database nd, long p)
        {
            if (nd.objects[tabledefpos] is Table tb)
            {
                var xs = tb.indexes;
                var ks = Domain.Row;
                for (var b = tb.indexes.First(); b != null; b = b.Next())
                    if (b.value().Contains(defpos))
                    {
                        if (b.value().Count == 1L)
                            xs -= b.key();
                        else
                            xs += (b.key(), b.value() - defpos);
                    }
                tb += (Table.Indexes, xs);
                nd += (tb, p);
            }
            if (nd.objects[reftabledefpos] is Table rt)
            {
                var xs = rt.rindexes;
                if (xs.Count == 1)
                    xs = CTree<long, CTree<Domain, Domain>>.Empty;
                else
                    xs -= defpos;
                rt += (Table.RefIndexes, xs);
                nd += (rt, p);
            }    
            return base.Drop(db, nd, p);
        }
        /// <summary>
        /// A readable version of the Index
        /// </summary>
        /// <returns>the string representation</returns>
        public override string ToString()
        {
            var sb = new StringBuilder(base.ToString());
            sb.Append(" count " + rows?.count);
            sb.Append(" Key:"); sb.Append(keys);
            sb.Append(" Kind="); sb.Append(flags);
            if (refindexdefpos != -1)
            {
                sb.Append(" RefIndex="); sb.Append(Uid(refindexdefpos));
                sb.Append(" RefTable="); sb.Append(Uid(reftabledefpos));
            }
            sb.Append(" Rows:"); sb.Append(rows);
            if (adapter!=-1)
            {
                sb.Append(" Adapter="); sb.Append(Uid(adapter));
                sb.Append(" References:"); sb.Append(references);
            }
            return sb.ToString();
        }
        internal override Basis New(BTree<long, object> m)
        {
            return new Index(defpos, m);
        }
        internal override DBObject New(long dp, BTree<long, object>m)
        {
            return new Index(dp, m);
        }
        protected override BTree<long, object> _Fix(Context cx, BTree<long, object> m)
        {
            var r = base._Fix(cx,m);
            var na = cx.Fix(adapter);
            if (na!=adapter)
            r += (Adapter, na);
            var nk = keys.Fix(cx);
            if (nk!=keys)
            r += (Keys, nk);
            var nr = cx.Fix(references);
            if (nr!=references)
            r += (References, nr);
            var ni = cx.Fix(refindexdefpos);
            if (refindexdefpos!=ni)
                r += (RefIndex, ni);
            var nt = cx.Fix(reftabledefpos);
            if (reftabledefpos!=nt)
                r += (RefTable, nt);
            return r;
        }
        internal override void Note(Context cx, StringBuilder sb)
        {
            sb.Append("// "); sb.Append(flags);
            var cm = "(";
            for (var b = keys.First(); b != null; b = b.Next())
                if (b.value() is long p)
                {
                    sb.Append(cm); cm = ",";
                    sb.Append(cx.NameFor(p));
                }
            sb.Append(")");
            if (flags.HasFlag(PIndex.ConstraintType.ForeignKey))
            { sb.Append(" "); sb.Append(cx.NameFor(reftabledefpos)); }
            sb.Append("\r\n");
        }
    }
    /*    /// <summary>
        /// A VirtualTable can have virtual indexes: they are for navigation properties
        /// and do not attempt to act as constraints on the remote table
        /// </summary>
        internal class VirtualIndex : Index
        {
            public VirtualIndex(VIndex pt,Context cx) :base(pt.defpos,_Mem(cx,pt))
            { }
            public VirtualIndex(long dp,BTree<long,object> m) : base(dp,m)
            { }
            static BTree<long,object> _Mem(Context cx,VIndex px)
            {
                var r = BTree<long, object>.Empty;
                r += (TableDefPos, px.tabledefpos);
                r += (IndexConstraint, px.flags);
                if (px.reference > 0 && cx.db.objects[px.reference] is Index rx)
                {
                    r += (RefIndex, rx.defpos);
                    r += (RefTable, rx.tabledefpos);
                }
                var cols = BList<long?>.Empty;
                var tb = (Table)cx.obs[px.tabledefpos];
                cx.Add(tb.framing);
                var dm = cx._Dom(tb);
                for (var b = px.seqs.First(); b != null; b = b.Next())
                {
                    var seq = b.value();
                    cols += dm.rowType[seq];
                }
                r += (Keys, cols);
                return r;
            }
            internal override Basis New(BTree<long, object> m)
            {
                return new VirtualIndex(defpos, m);
            }
            internal override DBObject Relocate(long dp)
            {
                return (dp == defpos) ? this : new VirtualIndex(dp, mem);
            }
            internal override void Cascade(Context cx, Drop.DropAction a = Level2.Drop.DropAction.Restrict, BTree<long, TypedValue> u = null)
            {
            }
        } */
}
