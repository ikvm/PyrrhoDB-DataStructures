using Pyrrho.Common;
using Pyrrho.Level3;
using Pyrrho.Level4;
using System.Collections.ObjectModel;
using System.Configuration;
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
namespace Pyrrho.Level2
{
	/// <summary>
	/// A View definition
	/// </summary>
    internal class PView : Compiled
    {
        /// <summary>
        /// The definition of the view
        /// </summary>
        public string viewdef = "";
        public override long Dependent(Writer wr, Transaction tr)
        {
            for (var b = framing.obs.First(); b != null; b = b.Next())
                if (b.value() is TableColumn tc)
                {
                    if (!Committed(wr, tc.tabledefpos))
                        return tc.tabledefpos;
                    if (!Committed(wr, tc.defpos))
                        return tc.defpos;
                }
            return -1;
        }
        /// <summary>
        /// Constructor: A view definition from the Parser
        /// </summary>
        /// <param name="tp">The PView type</param>
        /// <param name="nm">The name of the view</param>
        /// <param name="vd">The definition of the view</param>
        /// <param name="nst">The first possible framing object</param>
        /// <param name="pb">The physical database</param>
        /// <param name="curpos">The current position in the datafile</param>
        internal PView(string nm, string vd, Domain dm, long nst, long pp, Context cx)
            : this(Type.PView, nm, vd, dm, nst, pp, cx) 
        { }
        protected PView(Type pt,string nm,string vd, Domain dm, long nst, long pp, Context cx) 
            : base(pt,pp,cx,nm,dm,nst)
        {
            viewdef = vd;
        }
        /// <summary>
        /// Constructor: A view definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
        public PView(Reader rdr) : base(Type.PView, rdr) { }
        protected PView(Type tp, Reader rdr) : base(tp, rdr) { }
        protected PView(PView x, Writer wr) : base(x, wr)
        {
            viewdef = x.viewdef;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PView(this, wr);
        }

        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
        {
            wr.PutString(name);
            wr.PutString(viewdef);
            base.Serialise(wr);
        }
        /// <summary>
        /// deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            name = rdr.GetString();
            viewdef = rdr.GetString();
            base.Deserialise(rdr);
        }
        internal override void OnLoad(Reader rdr)
        {
            if (viewdef == "")
                return;
            var psr = new Parser(rdr,
                new Ident(viewdef, rdr.context.Ix(ppos + 2)));
            psr.Next(); psr.Next();  // VIEW name
            nst = psr.cx.db.nextStmt;
            var un = psr.ParseViewDefinition(name)??throw new PEException("0035");
            //       var cs = psr.ParseCursorSpecification(Domain.TableType);
            dataType = psr.cx._Dom(psr.cx.obs[un.defpos])??Domain.Null; // was cs.union
            psr.cx.result = un.defpos;
            framing = new Framing(psr.cx, nst);
        }
        internal virtual BTree<long,object> _Dom(Context cx,BTree<long,object>?m)
        {
            var d = 2 + dataType.depth;
            for (var b = dataType.rowType.First(); b != null && b.key()<dataType.display; 
                b = b.Next())
            if (b.value() is long p && framing.obs[p] is SqlValue c)
                    d = DBObject._Max(d, 1 + c.depth);
            /*            var rs = (RowSet)framing.obs[framing.result];
                        var ts = BList<long?>.Empty;
                        for (var b = rs.rsTargets.First(); b != null; b = b.Next())
                            ts += b.key(); */
            m ??= BTree<long, object>.Empty;
            if (dataType != null)
                m += (DBObject._Domain, dataType.defpos);
            m += (DBObject.Definer, cx.role.defpos);
            return m
                + (View.ViewPpos, ppos)
                + (DBObject._Framing, framing)
                + (DBObject._Depth, d);
        } 
        /// <summary>
        /// a readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
        public override string ToString()
        {
            var sb = new StringBuilder(GetType().Name);
            sb.Append(" ");sb.Append(name); 
            sb.Append(" "); sb.Append(DBObject.Uid(ppos));
            sb.Append(" "); sb.Append(viewdef);
            return sb.ToString();
        }
        public override DBException? Conflicts(Database db, Context cx, Physical that,PTransaction ct)
        {
            switch(that.type)
            {
                case Type.PTable1:
                case Type.PTable:
                    if (name == ((PTable)that).name)
                        return new DBException("40030", name, that, ct);
                    break;
                case Type.PView:
                case Type.RestView1:
                case Type.RestView2:
                case Type.RestView:
                    if (name == ((PView)that).name)
                        return new DBException("40012", name, that, ct);
                    break;
                case Type.Change:
                    if (name == ((Change)that).name)
                        return new DBException("40032", name, that, ct);
                    break;
            }
            return base.Conflicts(db, cx, that, ct);
        }
        internal override DBObject? Install(Context cx, long p)
        {
            var ro = cx.role;
            // The definer is the given role
            var priv = Grant.Privilege.Owner | Grant.Privilege.Insert | Grant.Privilege.Select |
                Grant.Privilege.Update | Grant.Privilege.Delete | 
                Grant.Privilege.GrantDelete | Grant.Privilege.GrantSelect |
                Grant.Privilege.GrantInsert |
                Grant.Privilege.Usage | Grant.Privilege.GrantUsage;
            var ti = new ObInfo(name, priv);
            var vw = new View(this, cx) + (DBObject.LastChange, p)
                + (DBObject.Infos, new BTree<long, ObInfo>(ro.defpos, ti));
            ro = ro + (Role.DBObjects, ro.dbobjects + (name, ppos));
            cx.db = cx.db + (ro,p)+ (vw,p);
            if (cx.db.mem.Contains(Database.Log))
                cx.db += (Database.Log, cx.db.log + (ppos, type));
            cx.Install(vw, p);
            return vw;
        }
        public override (Transaction?, Physical) Commit(Writer wr, Transaction? t)
        {
            var (tr, ph) = base.Commit(wr, t);
            if (this is PRestView)
                return (tr, ph);
            var pv = (PView)ph;
            var vw = ((DBObject)(tr?.objects[ppos] ?? throw new DBException("PE2402"))).Relocate(wr.cx);
            vw = (View)vw.New(vw.mem+(DBObject._Framing, pv.framing.Fix(wr.cx)));
            wr.cx.instDFirst = -1;
            return ((Transaction)(tr + (vw, tr.loadpos)), ph);
        }
    }
    internal class PRestView : PView
    {
        internal long structpos,usingtbpos = -1L;
        internal string? rname = null, rpass = null;
        internal long usingTableRowSet = -1L;
        internal BTree<string,long?> names = BTree<string,long?>.Empty;
        internal CTree<long,string> namesMap = CTree<long,string>.Empty;
        public override long Dependent(Writer wr, Transaction tr)
        {
            if (!Committed(wr, usingtbpos)) return usingtbpos;
            return -1;
        }
        public PRestView(Reader rdr) : this(Type.RestView, rdr) { }
        protected PRestView(Type t, Reader rdr) : base(t,rdr) { }
        public PRestView(string nm, long tp, Domain dm, long nst, long pp, Context cx)
            : this(Type.RestView, nm, tp, dm, nst, pp, cx) { }
        protected PRestView(Type t,string nm,long tp,Domain dm,long nst,long pp, Context cx)
            : base(t,nm,"",dm,nst,pp,cx)
        {
            structpos = tp;
            viewdef = dm.name;
            for (var b = dm.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.obs[p] is SqlValue c)
                {
                    names += (c.name ?? throw new PEException("PE2411"), p);
                    namesMap += (p, c.name);
                }
        }
        protected PRestView(PRestView x, Writer wr) : base(x, wr)
        {
            structpos = wr.cx.Fix(x.structpos);
            namesMap = wr.cx.Fix(x.namesMap);
            names = x.names;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PRestView(this, wr);
        }
        public override void Serialise(Writer wr)
        {
            wr.PutLong(-1L);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            rdr.GetLong();
            base.Deserialise(rdr);
        }
        internal override BTree<long, object> _Dom(Context cx, BTree<long, object>? m)
        {
            return m??BTree<long,object>.Empty;
        }
        internal override void OnLoad(Reader rdr)
        {
            var psr = new Parser(rdr.context, viewdef);
            nst = psr.cx.db.nextStmt;
            structpos = nst;
            var m = psr.ParseRowTypeSpec(Sqlx.VIEW).mem;
            if (psr.cx.obs[structpos] is not Table tb)
                return; 
            tb += (VirtualTable._RestView, ppos);
            dataType = new Domain(tb.domain, m);
            rdr.context.Add(dataType);
            framing = new Framing(psr.cx,nst);
            tb += (DBObject._Framing, framing);
            rdr.context.Add(tb);
            rdr.context.db+= (Database.NextStmt,psr.cx.db.nextStmt);
        }
        internal override DBObject? Install(Context cx, long p)
        {
            var ro = cx.role;
            if (cx._Ob(structpos) is not VirtualTable vt)
                throw new DBException("42105");
            // The definer is the given role
            var priv = Grant.Privilege.Owner | Grant.Privilege.Insert | Grant.Privilege.Select |
                Grant.Privilege.Update | Grant.Privilege.Delete |
                Grant.Privilege.GrantDelete | Grant.Privilege.GrantSelect |
                Grant.Privilege.GrantInsert |
                Grant.Privilege.Usage | Grant.Privilege.GrantUsage;
            var ti = new ObInfo(name, priv);
            ti += (ObInfo.SchemaKey, p);
            vt = vt + (VirtualTable._RestView, ppos)
                + (DBObject._Domain, dataType.defpos) + (DBObject.LastChange, p)
                + (DBObject.Infos, new BTree<long,ObInfo>(ro.defpos, ti));
            ro = ro + (Role.DBObjects, ro.dbobjects + (name, ppos));
            var fr = framing + vt;
            for (var b = vt.framing.obs.First(); b != null; b = b.Next())
                fr += b.value();
            fr += dataType;
            var rv = new RestView(this, cx) + (DBObject._Framing,fr);
            cx._Add(rv);
            cx.db = cx.db + (ro, p) + (rv, p) + (vt,p);
            cx.Install(rv, p);
       //     cx.Add(rv.framing);
            return rv;
        }
        public override string ToString()
        {
            return "PRestView "+name + "["+DBObject.Uid(structpos)+"]" + viewdef;
        }
    }
    /// <summary>
    /// This class is deprecated: credentials information can be safely provided in URL
    /// </summary>
    internal class PRestView1 : PRestView
    {
        public PRestView1(Reader rdr) : base(Type.RestView1, rdr) { }
        public PRestView1(string nm, long tp, Domain dm, string rnm, string rpw, long nst, long pp, 
            Context cx) : base(Type.RestView1, nm, tp, dm, nst, pp, cx)
        {
            rname = rnm;
            rpass = rpw;
        }
        protected PRestView1(PRestView1 x, Writer wr) : base(x, wr)
        {
            rname = x.rname;
            rpass = x.rpass;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PRestView1(this, wr);
        }

        public override void Serialise(Writer wr)
        {
            wr.PutString(rname??"");
            wr.PutString(rpass??"");
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            rname = rdr.GetString();
            rpass = rdr.GetString();
            base.Deserialise(rdr);
        }
        public override string ToString()
        {
            return "PRestView1 " + name + "(" + structpos + ") '" +rname+"':'"+rpass +"'";
        }
    }
    internal class PRestView2 : PRestView
    {
        public PRestView2(Reader rdr) : base(Type.RestView2, rdr) { }
        public PRestView2(string nm, long tp, Domain dm, long nst, RowSet uf, long pp, Context cx)
            : base(Type.RestView2, nm, tp, dm, nst, pp, cx)
        {
            usingtbpos = uf.target;
            usingTableRowSet = uf.rsTargets.First()?.value()??-1L;
            FixCols(cx);
            framing = new Framing(cx,nst);
        }
        protected PRestView2(PRestView2 x, Writer wr) : base(x, wr)
        {
            usingtbpos = wr.cx.Fix(x.usingtbpos);
            usingTableRowSet = wr.cx.Fix(x.usingTableRowSet);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PRestView2(this, wr);
        }

        public override void Serialise(Writer wr)
        {
            wr.PutLong(usingtbpos);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            usingtbpos = rdr.GetLong();
            base.Deserialise(rdr);
        }
        public override DBException? Conflicts(Database db, Context cx, Physical that, PTransaction ct)
        {
            if (that.type == Type.Drop && usingtbpos == ((Drop)that).delpos)
                return new DBException("40012",usingtbpos, that, ct);
            return base.Conflicts(db, cx, that, ct);
        }
        // Identify the remote columns of the restview and adjust the framing
        void FixCols(Context cx)
        {
            var vs = BTree<string, DBObject>.Empty;
            for (var b = dataType.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.obs[p] is DBObject c)
                    vs += (c.NameFor(cx), c);
            var ts = (TableRowSet)(cx.obs[usingTableRowSet] ?? throw new PEException("PE2421"));
            for (var b = cx._Dom(ts)?.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.obs[p] is DBObject c && vs[c.NameFor(cx)] is DBObject oc)
                    cx.Replace(c, oc);
        }
        void NFixCols(Context cx)
        {
            var vs = BTree<string, DBObject>.Empty;
            for (var b = dataType.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.obs[p] is DBObject c)
                    vs += (c.NameFor(cx), c);
            var ts = (TableRowSet)(cx.obs[usingTableRowSet] ?? throw new PEException("PE2422"));
            var ns = BList<DBObject>.Empty;
            var si = BTree<long, long?>.Empty; // TableColumn,SqlValue
            var im = BTree<long, long?>.Empty; // SqlValue,TableColumn
            for (var b = cx._Dom(ts)?.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.obs[p] is DBObject c && ts.iSMap[p] is long tp)
                {
                    if (vs[c.NameFor(cx)] is DBObject oc)
                    {
                        var nc = c.Relocate(oc.defpos);
                        cx.Add(nc);
                        ns += (SqlValue)nc;
                        si += (tp, oc.defpos);
                        im += (oc.defpos, ts.iSMap[p]);
                    }
                    else
                    {
                        ns += (SqlValue)c;
                        si += (tp, p);
                        im += (p, ts.iSMap[p]);
                    }
                }
            var nd = new Domain(Sqlx.TABLE, cx, ns, (int)ns.Count - 1);
            cx._Add(nd);
            ts += (DBObject._Domain, nd.defpos);
            ts += (RowSet.ISMap, im);
            ts += (RowSet.SIMap, si);
            cx._Add(ts);
        }
        internal override void OnLoad(Reader rdr)
        {
            var cx = rdr.context;
            var db = cx.db;
            var ro = cx.role;
            nst = db.nextStmt;
            base.OnLoad(rdr);
            if (db.objects[usingtbpos] is not Table ut ||
                ut.infos[ro.defpos] is not ObInfo oi || oi.name == null)
                throw new PEException("PE43100");
            var ic = new Ident(oi.name, cx.GetIid());
            usingTableRowSet = ut.RowSets(ic,cx,dataType,ic.iix.dp).defpos;
            NFixCols(cx);
            framing = new Framing(cx,nst);
        }
        public override string ToString()
        {
            return "PRestView2 " + name + "(" + DBObject.Uid(structpos) + ") using " + usingtbpos;
        }
    }
}
