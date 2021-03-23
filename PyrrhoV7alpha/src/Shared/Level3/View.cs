using Pyrrho.Common;
using Pyrrho.Level2;
using Pyrrho.Level4;
using System.Collections.Generic;
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Configuration;
using System.Diagnostics.Eventing.Reader;
using System.Net.NetworkInformation;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2021
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
	/// A database object for a view.
    /// The domain is computed from the pv.viewdef immediately.
    /// Immutable
    /// However, we want to optimise queries deribed from views, so
    /// we use the second constructor to make a private immutable copy
    /// of the committed version.
	/// </summary>
	internal class View : DBObject
	{
        internal const long
            Targets = -154, // CList<long>  Table/View
            ViewCols = -378, // CTree<string,long> SqlValue
            ViewDef = -379, // string
            ViewPpos = -377;// long View
        public string name => (string)mem[Name];
        public string viewDef => (string)mem[ViewDef];
        public CTree<string, long> viewCols =>
            (CTree<string, long>)mem[ViewCols] ?? CTree<string, long>.Empty;
        public CList<long> targets =>
            (CList<long>)mem[Targets] ?? CList<long>.Empty;
        public long viewPpos => (long)(mem[ViewPpos] ?? -1L);
        public View(PView pv,Database db,BTree<long,object>m=null) 
            : base(pv.ppos, pv._Dom(db,m)
                  + (Name,pv.name) + (Definer,db.role.defpos)
                  + (ViewDef,pv.viewdef) 
                  + (LastChange, pv.ppos))
        { }
        protected View(long dp, BTree<long, object> m) : base(dp, m) { }
        public static View operator+(View v,(long,object)x)
        {
            return (View)v.New(v.mem + x);
        }
        internal override ObInfo Inf(Context cx)
        {
            throw new NotImplementedException();
        }
        internal override void _Add(Context cx)
        {
            cx.Install1(framing);
            base._Add(cx);
        }
        internal override CList<long> _Cols(Context cx)
        {
            return domain.rowType;
        }
        /// <summary>
        /// This routine prepares a fresh copy of a View reference, first
        /// injecting assignments and filters into the View structure.
        /// Views are compiled objects, but queries containing them
        /// provide filters, build them into joins, use them in aggregations
        /// and subqueries etc, and in order to optimise the resulting
        /// rowsets we need to ensure that we have fresh uids for exposed
        /// data (because we may have several independent references to the view).
        /// Once we have decided which obuids and rsuids need instancing
        /// the Fix(cx) routines will create the transformed versions of the
        /// compiled pieces. We avoid recursion using the following method:
        /// We begin by discovering the dependencies of the pieces D(K,V,F)
        /// (called obrefs in the code) such that in D, K refers to V, and 
        /// F (enum VIC): OK=K is an object, RK=K a rowset,and same for OV, RV.
        /// Once built we invert the tree to D'(V,K,F) (called refobs).
        /// We start to Instance the references we know about, but
        /// every time we add a V to obuids/rsuids we cascade to instance K's 
        /// in method OnInstance. Once obuids and rsuids are built we call Fix.
        /// </summary>
        /// <param name="cx"></param>
        /// <param name="f"></param>
        /// <returns></returns>
        internal RowSet Instance(Context cx,RowSet rv)
        {
            var oob = cx.obuids;
            var oor = cx.rsuids;
            var oof = cx.obrefs;
            var st = framing.result;
            cx.affected = framing.withRvv;
            if (cx.srcFix == 0)
                cx.srcFix = cx.db.lexeroffset;
            cx.obuids = BTree<long, long?>.Empty;
            cx.rsuids = BTree<long, long?>.Empty;
            // start to build the dependency tree: begin with the View and rowType
            var t = BTree<long, VIC?>.Empty;
            t = Scan(t);
            t = Scan(t, rv.rt, VIC.OK | VIC.OV);
            cx.obrefs = new BTree<long,BTree<long,VIC?>>(defpos,t);
            // scan the compiled rowsets
            for (var rb = framing.data.First();rb!=null;rb=rb.Next())
            {
                var rs = rb.value();
                t = cx.obrefs[rb.key()] ?? BTree<long, VIC?>.Empty;
                // for rowsets add dependencies for ambient wheres and assignments
                for (var b = rv.where.First(); b != null; b = b.Next())
                    if (((SqlValue)cx.obs[b.key()]).KnownBy(cx, rs))
                        t += (b.key(), VIC.RK|VIC.OV);
                for (var b=rv.assig.First();b!=null;b=b.Next())
                {
                    var u = b.key();
                    if (rs.Knows(cx, u.vbl) && ((SqlValue)cx.obs[u.val]).KnownBy(cx, rs))
                    {
                        t += (u.vbl, VIC.RK | VIC.OV);
                        t += (u.val, VIC.RK | VIC.OV);
                    } 
                }
                // now add all internal dependencies: helper methods are in DBObject
                cx.obrefs += (rs.defpos,rs.Scan(t));
            }
            // scan the compiled objects
            for (var rb = framing.obs.First(); rb != null; rb = rb.Next())
            {
                var ob = rb.value();
                t = cx.obrefs[ob.defpos] ?? BTree<long, VIC?>.Empty;
                // add all internal dependencies
                cx.obrefs += (ob.defpos, ob.Scan(t));
            }
            // now invert the tree to prepare for the cascade
            var refobs = BTree<long,BTree<long,VIC?>>.Empty;
            for (var b = cx.obrefs.First(); b != null; b = b.Next())
                for (var c = b.value()?.First(); c != null; c = c.Next())
                {
                    var ck = c.key();
                    t = refobs[ck] ?? BTree<long, VIC?>.Empty;
                    t += (b.key(), c.value());
                    refobs += (ck, t);
                }
            InstanceOb(cx, defpos, refobs);
            InstanceRS(cx, st, refobs);
            // Now consider the ambient rowtype
            var fb = rv.rt.First();
            var rf = cx.data[st];
            for (var vb = rf.rt.First(); vb != null; vb = vb.Next()) 
            {
                var p = vb.value();
                if (fb != null) // instance for the display items
                {
                    cx.obuids += (p, fb.value());
                    fb = fb.Next();
                }
                else // make new SqlCopys for the non-display items
                {
                    var cp = cx.Next(p);
                    var sv = (SqlValue)cx.obs[p];
                    cx.obuids += (p, cp);
                    cx._Add(new SqlCopy(cp, cx, sv.name, rf.defpos, p));
                }
                OnInstance(cx, p, refobs);
            }
            // Cascade to build obuids, rsuids has finished. Fix() everything.
            for (var b = cx.obs.PositionAt(defpos); b != null; b = b.Next())
            {
                var p = b.key();
                var np = cx.obuids[p] ?? p;
                var nb = (DBObject)b.value().Fix(cx);
                if (nb != b.value() && p == nb.defpos && cx.parse==ExecuteStatus.Obey
                    && (p<Transaction.TransPos 
                    || (p>=Transaction.Executables && p<Transaction.HeapStart)))
                    throw new PEException("PE588");
                cx.obs += (np, nb);
            }
            for (var b = cx.data.PositionAt(defpos+1); b != null; b = b.Next())
            {
                var p = b.key();
                var np = cx.rsuids[p] ?? p;
                var nr = (RowSet)b.value().Fix(cx);
                if (nr != b.value() && p == nr.defpos && cx.parse==ExecuteStatus.Obey
                    && (p < Transaction.TransPos
                    || (p >= Transaction.Executables && p < Transaction.HeapStart)))
                    throw new PEException("PE589");
                cx.data += (np, nr);
            }
            // All done, so finish up
            var vp = cx.rsuids[rf.defpos].Value;
            cx.obuids = oob;
            cx.rsuids = oor;
            cx.obrefs = oof;
            var r = cx.data[vp];
            r += (_Domain, r.domain + (Domain.Display, rv.display));
            cx.data += (rv.defpos, r);
            return r;
        }
        void OnInstance(Context cx,long dp, BTree<long, BTree<long, VIC?>> ro)
        {
            for (var b = ro[dp]?.First(); b != null; b = b.Next())
            {
                var k = b.key(); // K of original D(K,V,F) table
                var f = b.value() ?? VIC.None;
                if (f.HasFlag(VIC.OK))
                    InstanceOb(cx, k, ro);
                if (f.HasFlag(VIC.RK))
                    InstanceRS(cx, k, ro);
            }
        }
        void InstanceOb(Context cx,long dp,BTree<long,BTree<long,VIC?>> ro)
        {
            if (cx.obuids.Contains(dp))
                return;
            var np = cx.Next(dp);
            cx.obuids += (dp, np);
            cx._Add(cx.obs[dp].Relocate(np));
            OnInstance(cx, dp, ro);
        }
        void InstanceRS(Context cx,long dp, BTree<long,BTree<long,VIC?>> ro)
        {
            if (cx.rsuids.Contains(dp))
                return;
            var np = cx.Next(dp);
            cx.rsuids += (dp, np);
            cx.data +=(np, (RowSet)cx.data[dp].Relocate(np));
            OnInstance(cx, dp, ro);
        }
        /// <summary>
        /// Triggered on the complete set if a view is referenced in a From.
        /// The following tasks should get carried out by Instance in Apply/Review
        /// 3.	A view column can be dropped from the request if nobody references it.
        /// 4.	If a view column is used as a simple filter, 
        /// we can pass the filter to the target, 
        /// and simplify everything by using the constant value.
        /// 5.	If a view column is aggregated, 
        /// we can perform some or all of the aggregation on the target, 
        /// but we may need to group by the other visible remote columns.
        /// 6.	With joins we need to preserve columns referenced in the join condition, 
        /// and keep track of keys. Then perform the join with the target instead.
        /// </summary>
        /// <param name="cx"></param>
        internal override void RowSets(Context cx,From f,CTree<long,RowSet.Finder>fi)
        {
            cx.Install2(framing);
            var rf = Instance(cx, new VirtualRowSet(cx, f, fi));
            cx.data += (f.defpos, rf);
        }
        /// <summary>
        /// API development support: generate the C# information for a Role$Class description
        /// </summary>
        /// <param name="from">the From</param>
        /// <param name="_enu">the bookmark in the RoleObjects enumeration</param>
        /// <returns></returns>
        internal override TRow RoleClassValue(Transaction tr,DBObject from, ABookmark<long, object> _enu)
        {
            var md = _enu.value() as View;
            var mi = tr.role.infos[md.defpos] as ObInfo;
            var ro = tr.role;
            var sb = new StringBuilder("using System;\r\nusing Pyrrho;\r\n");
            sb.Append("\r\n[Schema("); sb.Append(from.lastChange); sb.Append(")]");
            sb.Append("\r\n/// <summary>\r\n");
            sb.Append("/// Class " + mi.name + " from Database " + tr.name + ", Role " + tr.role.name + "\r\n");
            if (mi.description != "")
                sb.Append("/// " + mi.description + "\r\n");
            sb.Append("/// </summary>\r\n");
            sb.Append("public class " + mi.name + " : Versioned {\r\n");
            mi.DisplayType(tr,sb);
            sb.Append("}\r\n");
            return new TRow(mi,
                new TChar(mi.name),
                new TChar(""),
                new TChar(sb.ToString()));
        } 
        /// <summary>
        /// API development support: generate the Java information for a Role$Java description
        /// </summary>
        /// <param name="from">the From</param>
        /// <param name="_enu">the bookmark in the RoleObjects enumeration</param>
        /// <returns></returns>
        internal override TRow RoleJavaValue(Transaction tr, DBObject from, ABookmark<long, object> _enu)
        {
            var md = _enu.value() as View;
            var mi = tr.role.infos[md.defpos] as ObInfo;
            var ro = tr.role;
            var sb = new StringBuilder();
            sb.Append("\r\n/* \r\n * Class "); sb.Append(mi.name); sb.Append(".java\r\n");
            sb.Append("import org.pyrrhodb.*;\r\n");
            sb.Append("\r\n@Schema("); sb.Append(from.lastChange); sb.Append(")");
            sb.Append("\r\n/**\r\n *\r\n * @author "); sb.Append(tr.user.name); sb.Append("\r\n */");
            sb.Append("\r\n * from Database " + tr.name + ", Role " + tr.role.name + "\r\n");
            if (mi.description != "")
                sb.Append(" * " + mi.description + "\r\n");
            sb.Append(" */\r\n");
            sb.Append("public class " + mi.name + " extends Versioned {\r\n");
            DisplayJType(tr,mi, sb);
            sb.Append("}\r\n");
            return new TRow(mi,
                new TChar(mi.name),
                new TChar(""),
                new TChar(sb.ToString()));
        }
        /// <summary>
        /// API development support: generate the Python information for a Role$Python description
        /// </summary>
        /// <param name="from">the From</param>
        /// <param name="_enu">the bookmark in the RoleObjects enumeration</param>
        /// <returns></returns>
        internal override TRow RolePythonValue(Transaction tr, DBObject from, ABookmark<long, object> _enu)
        {
            var md = _enu.value() as View;
            var mi = tr.role.infos[md.defpos] as ObInfo;
            var sb = new StringBuilder();
            sb.Append("# "); sb.Append(mi.name); sb.Append(" Created on ");
            sb.Append(DateTime.Now);
            sb.Append("\r\n# from Database " + tr.name + ", Role " + tr.role.name + "\r\n");
            if (mi.description != "")
                sb.Append("# " + mi.description + "\r\n");
            sb.Append("class " + mi.name + ":\r\n");
            sb.Append(" def __init__(self):\r\n");
            DisplayPType(tr,mi, sb);
            return new TRow(mi,
                new TChar(mi.name),
                new TChar(""),
                new TChar(sb.ToString()));
        }
        /// <summary>
        /// API development support: generate the Java type information for a field 
        /// </summary>
        /// <param name="dt">the data type</param>
        /// <param name="sb">a string builder</param>
        /// <param name="kc">key information</param>
        static void DisplayJType(Transaction tr,ObInfo dt, StringBuilder sb)
        {
            var i = 0;
            for (var b = dt.domain.rowType.First();b!=null;b=b.Next(),i++)
            {
                var p = b.value();
                var c = (ObInfo)tr.role.infos[b.value()];
                var cd = c.domain;
                var n = c.name.Replace('.', '_');
                var tn = c.name;
                if (cd.kind != Sqlx.TYPE && cd.kind != Sqlx.ARRAY && cd.kind != Sqlx.MULTISET)
                    tn = cd.SystemType.Name;
                if (cd.kind == Sqlx.ARRAY || cd.kind == Sqlx.MULTISET)
                {
                    if (tn == "[]")
                        tn = "_T" + i + "[]";
                    if (n.EndsWith("("))
                        n = "_F" + i;
                }
                FieldType(tr,sb,cd);
                sb.Append("  public " + tn + " " + n + ";\r\n");
            }
            i = 0;
            for (var b=dt.domain.rowType.First();b!=null;b=b.Next(),i++)
            {
                var c = (ObInfo)tr.role.infos[b.value()];
                var cd = c.domain;
                if (cd.kind != Sqlx.ARRAY && cd.kind != Sqlx.MULTISET)
                    continue;
                cd = cd.elType;
                var tn = c.name;
                if (tn != null)
                    sb.Append("/* Delete this declaration of class " + tn + " if your app declares it somewhere else */\r\n");
                else
                    tn += "_T" + i;
                sb.Append("  public class " + tn + " extends Versioned {\r\n");
                DisplayJType(tr, tr.role.infos[c.defpos] as ObInfo, sb);
                sb.Append("  }\r\n");
            }
        }
        /// <summary>
        /// API development support: generate the Python type information for a field 
        /// </summary>
        /// <param name="dt">the data type</param>
        /// <param name="sb">a string builder</param>
        /// <param name="kc">key information</param>
        static void DisplayPType(Transaction tr,ObInfo dt, StringBuilder sb)
        {
            var i = 0;
            for (var b=dt.domain.rowType.First();b!=null;b=b.Next(),i++)
            {
                var c = (ObInfo)tr.role.infos[b.value()];
                var cd = c.domain;
                var n = c.name.Replace('.', '_');
                var tn = c.name;
                if (cd.kind != Sqlx.TYPE && cd.kind != Sqlx.ARRAY && cd.kind != Sqlx.MULTISET)
                    tn = cd.SystemType.Name;
                if (cd.kind == Sqlx.ARRAY || cd.kind == Sqlx.MULTISET) // ??
                {
                    if (tn == "[]")
                        tn = "_T" + i + "[]";
                    if (n.EndsWith("("))
                        n = "_F" + i;
                }
                sb.Append("  self." + n + " = " + cd.defaultValue+ "\r\n");
            }
        }
        internal override void Modify(Context cx, Modify m, long p)
        {
            cx.db = cx.db + (this + (ViewDef, m.body), p) + (Database.SchemaKey,p);
        }
        internal override Basis New(BTree<long, object> m)
        {
            return new View(defpos, m);
        }
        internal override DBObject New(Context cx, BTree<long, object> m)
        {
            if (defpos >= Transaction.Analysing || cx.parse == ExecuteStatus.Parse)
                return (m == mem) ? this : (View)New(m);
            return cx.Add(new View(cx.GetUid(), m));
        }
        internal override DBObject Relocate(long dp)
        {
            return new View(dp, mem);
        }
        internal override Basis Fix(Context cx)
        {
            var r = base.Fix(cx);
            var ts = cx.Fix(targets);
            if (ts!=targets)
                r += (Targets, ts);
            var dm = domain.Fix(cx);
            if (domain != dm)
                r += (_Domain, dm);
            var nv = cx.Fix(viewCols);
            if (nv != viewCols)
                r += (ViewCols, nv);
            return r;
        }
        internal override Basis _Relocate(Writer wr)
        {
            var r = (View)base._Relocate(wr);
            var ts = wr.Fix(targets);
            if (ts != targets)
                r += (Targets, ts);
            var cs = wr.Fix(viewCols);
            if (cs != viewCols)
                r += (ViewCols, cs);
            return r;
        }
        internal override DBObject _Replace(Context cx, DBObject so, DBObject sv)
        {
            if (cx.done.Contains(defpos))
                return cx.done[defpos];
            var r = (View)base._Replace(cx, so, sv);
            var ch = false;
            var ts = CList<long>.Empty;
            for (var b=targets.First();b!=null;b=b.Next())
            {
                var tg = b.value();
                var nt = cx.Replace(tg, so, sv);
                ch = nt != tg;
                ts += nt;
            }
            if (ch)
                r += (Targets, ts);
            var cs = BTree<string, long>.Empty;
            for (var b=viewCols?.First();b!=null;b=b.Next())
            {
                var p = cx.Replace(b.value(),so,sv);
                cs += (b.key(), p);
                if (p != b.value())
                    ch = true;
            }
            if (ch)
                r += (ViewCols, cs);
            if (domain.representation.Contains(so.defpos))
                r += (_Domain, domain._Replace(cx,so, sv));
            cx.done+=(defpos,r);
            return r;
        }
        internal override BTree<long, VIC?> Scan(BTree<long, VIC?> t)
        {
            t = Scan(t, targets, VIC.OK | VIC.OV);
            t = Scan(t, viewCols, VIC.OK | VIC.OV);
            return t;
        }
        /// <summary>
        /// a readable version of the View
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString()
        {
            var sb = new StringBuilder(base.ToString());
            sb.Append(" Query "); sb.Append(viewDef);
            sb.Append(" Ppos: "); sb.Append(Uid(viewPpos));
            sb.Append(" Cols (");
            var cm = "";
            for (var b=viewCols.First();b!=null;b=b.Next())
            {
                sb.Append(cm); cm = ",";
                sb.Append(b.key()); sb.Append("=");
                sb.Append(Uid(b.value()));
            }
            sb.Append(") "); sb.Append(domain);
            cm = " Targets: ";
            for (var b=targets.First();b!=null;b=b.Next())
            { sb.Append(cm); cm = ","; sb.Append(Uid(b.value())); }
            return sb.ToString();
        }
    }
    /// <summary>
    /// RestViews get their data from a REST service.
    /// The parser sets up the restview target in the global From.
    /// During Selects, based on the enclosing QuerySpecification,
    /// we work out a CursorSpecification for the From.source
    /// and a From for the usingTable if any, in the usingFrom field of the From.source CS.
    /// Thus there are four Queries involved in RestView evaluation, here referred to
    /// as QS,GF,CS and UF. Where example comlumns such as K.F occur below we understand
    /// there there may in general be more than one of each sort.
    /// All columns coming from QS maintain their positions in the final result rowSet.
    /// But their evaluation rules and names change: the alias field will hold the original
    /// name if there is no alias. 
    /// The rules are quite complicated:
    /// If QS contains no aggregation columns, GF and FS have the same columns and evaluation rules:
    /// and current values of usingTable columns K are supplied as literals in FS column exps.
    /// If there is no alias for a column expr, an alias is constructed naming K.
    /// Additional (non-grouped) columns will be added to CS,GF for AVG etc.
    /// GS will always have the same grouping columns as QS. For example
    /// QS (AVG(E+K),F) group by F -> 
    ///     CS (SUM(E+[K]) as C_2,F COUNT(E+[K]) as D_2), [K] is the current value of K from UF
    ///     GF (SUM(C_2) as C2,F,COUNT(D_2) as D2) 
    ///         -> QS(C2/D2 as "AVG(E+K)", F)
    /// </summary>
    internal class RestView : View
    {
        internal const long
            ClientName = -381, // string, deprecated
            ClientPassword = -382, // string, deprecated
            Mime = -255, // string
            SqlAgent = -256, // string
            UsingTablePos = -385, // long Table
            ViewStruct = -386,// Domain
            ViewTable = -371; // long   Table
        internal string nm => (string)mem[ClientName];
        internal string pw => (string)mem[ClientPassword]; // deprecated
        internal Domain viewStruct => (Domain)mem[ViewStruct];
        internal long viewTable => (long)(mem[ViewTable] ?? -1L);
        internal string mime => (string)mem[Mime];
        internal string sqlAgent => (string)mem[SqlAgent];
        internal string clientName => (string)mem[ClientName];
        internal string clientPassword => (string)mem[ClientPassword];
        internal long usingTable => (long)(mem[UsingTablePos]??-1L);
        /// <summary>
        /// Constructor: a RestView from level 2
        /// </summary>
        /// <param name="pv">The PRestView</param>
        /// <param name="ro">the current (definer's) role</param>
        /// <param name="ow">the owner</param>
        /// <param name="rs">the list of grantees</param>
        public RestView(PRestView pv,Database db) : base(pv,db,_Mem(pv,db)
            +(ViewStruct,((ObInfo)db.role.infos[pv.structpos]).domain)
            +(UsingTablePos,pv.usingtbpos)+(ViewTable,pv.structpos)
            +(ClientName,pv.rname)+(ClientPassword,pv.rpass))
        { }
        protected RestView(long dp, BTree<long, object> m) : base(dp, m) 
        { }
        static BTree<long,object> _Mem(PRestView pv,Database db)
        {
            var r = BTree<long, object>.Empty;
            var vc = BTree<string, long>.Empty;
            var d = 2;
            var tb = (Table)db.objects[pv.structpos];
            d = Math.Max(d, tb.depth);
            for (var b = tb.domain.rowType.First(); b != null; b = b.Next())
            {
                var ci = (ObInfo)db.role.infos[b.value()];
                vc += (ci.name, b.value());
            }
            r += (ViewCols, vc);
            r += (Depth, d);
            return r;
        }
        internal override Basis New(BTree<long, object> m)
        {
            return new RestView(defpos,m);
        }
        internal override DBObject New(Context cx, BTree<long, object> m)
        {
            if (defpos >= Transaction.Analysing || cx.parse == ExecuteStatus.Parse)
                return (m == mem) ? this : (RestView)New(m);
            return cx.Add(new RestView(cx.GetUid(), m));
        }
        internal override Database Add(Database d, PMetadata pm, long p)
        {
            var oi = ((ObInfo)d.role.infos[defpos])+(ObInfo._Metadata,pm.Metadata());
            var ro = d.role + (defpos, oi);
            d += (ro,p); 
            return base.Add(d, pm, p);
        }
        public static RestView operator +(RestView r, (long, object) x)
        {
            return new RestView(r.defpos, r.mem + x);
        }
        internal override DBObject Relocate(long dp)
        {
            return new RestView(dp,mem);
        }
        internal override Basis _Relocate(Writer wr)
        {
            if (defpos < wr.Length)
                return this;
            var r = base._Relocate(wr);
            r += (ViewStruct, domain._Relocate(wr));
            r += (UsingTablePos, wr.Fix(usingTable));
            return r;
        }
        internal override Basis Fix(Context cx)
        {
            var r = (RestView)base.Fix(cx);
            var nv = viewStruct.Fix(cx);
            if (nv != viewStruct)
                r += (ViewStruct, nv);
            var nt = cx.obuids[usingTable] ?? usingTable;
            if (nt != usingTable)
                r += (UsingTablePos, nt);
            return r;
        }
        internal override void RowSets(Context cx, From gf, CTree<long, RowSet.Finder> fi)
        {
            RestRowSet r = new RestRowSet(cx, gf, this);
            var dp = r.defpos;
            if (cx.db.objects[usingTable] is Table tb)
            {
                cx._Add(tb);
                var n = tb.domain.Length - 1; // usingCols length
                var np = cx.GetUid(); // restrowset new position
                var a = ((ObInfo)cx.db.role.infos[tb.defpos]).name; // tb.name
                var uf = new From(new Ident(a,cx.GetUid()), cx, tb).RowSets(cx, fi); // SelectedRowSet
                var rc = CTree<string, long>.Empty; // remotecols
                var us = CTree<string,long>.Empty; // catalogue of using columns
                // usingcols are all columns from tb except the last
                for (var b=uf.rt.First();b!=null && b.key()<n;b=b.Next())
                {
                    var p = b.value();
                    var sc = (SqlCopy)cx.obs[p];
                    us += (sc.name,p);
                }
                var ju = CTree<long,long>.Empty; // joinUsing
                var uc = CTree<string, long>.Empty; // usingCols
                // scan gf rowType to get remote columns and match up using cols
                for (var b = r.rt.First(); b != null; b = b.Next())
                {
                    var p = b.value();
                    var sv = (SqlValue)cx.obs[p];
                    if (us.Contains(sv.name))
                    {
                        var q = us[sv.name]; // match and translate uids
                        ju += (q, p);
                        uc += (sv.name, p);
                        fi += (p, new RowSet.Finder(q, uf.defpos));
                        fi += (q, new RowSet.Finder(q, uf.defpos));
                    }
                    else
                    {
                        rc += (sv.name, p); // column is remote
                        fi += (p, new RowSet.Finder(p, np));
                    }
                }
                var ul = uf.rt.Last().value();
                var nr = (RowSet)(r+(RestRowSet.UrlCol,ul)
                    +(RestRowSet.RemoteCols,rc)+(Index.Keys,r.rt)
                    +(RestRowSet.UsingCols,uc)+(RowSet._Finder,fi)
                    +(JoinPart.JoinUsing,ju)
                    +(RestRowSet.UsingTable,uf.defpos)).Relocate(np);
                cx.withViews -= r.defpos;
                cx.withViews += (np,true);
                var jp = new JoinPart(r.defpos) + (_Domain, r.domain)
                    + (JoinPart.LeftOperand, uf.defpos)
                    + (JoinPart.RightOperand, nr.defpos)
                    + (JoinPart.Natural, Sqlx.USING)
                    + (JoinPart.JoinUsing, ju);
                var jr = new JoinRowSet(cx,jp,uf, nr);
                cx.data += (nr.defpos, nr);
                cx.data += (dp, jr);
                return;
            }
            cx.withViews += (dp, true);
            cx.data += (dp, r);
        }
    }
}
