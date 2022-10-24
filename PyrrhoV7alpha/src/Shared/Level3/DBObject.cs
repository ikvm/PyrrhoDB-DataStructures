using System.Collections.Generic;
using System.Text;
using Pyrrho.Level2;
using Pyrrho.Common;
using Pyrrho.Level4;
using System;
using System.Configuration;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2022
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
    /// DBObject is the base class for Level 3 database objects (e.g. Table, Role, Procedure, Domain)
    /// Immutable
    /// // shareable as of 26 April 2021
    /// </summary>
    internal abstract class DBObject : Basis
    {
        /// <summary>
        /// The uid of the abstract object this is or affects
        /// </summary>
        public readonly long defpos;
        internal const long
            _Alias = -62, // string        
            Classification = -63, // Level
            Definer = -64, // long Role
            Defpos = -257, // long for Rest service
            Dependents = -65, // CTree<long,bool> Non-obvious objects that need this to exist
            _Depth = -66, // int  (max depth of dependents)
            _Domain = -176, // long Domain
            _Framing = -167, // Framing
            _From = -306, // long From
            _Ident = -409, // Ident (used in ForwardReference and RowSet)
            Infos = -126, // BTree<long,ObInfo> Role
            LastChange = -68, // long (formerly called Ppos)
            Owner = -59, // long
            Sensitive = -69; // bool
        /// <summary>
        /// During transaction execution, many DBObjects have aliases.
        /// Aliases do not form part of renaming machinery
        /// </summary>
        internal string alias => (string)mem[_Alias];
   //     internal Domain dataType => (Domain)mem[_DataType];
        /// <summary>
        /// The definer of the object (a Role)
        /// </summary>
        public long definer => (long)(mem[Definer] ?? -1L);
        public long owner => (long)(mem[Owner] ?? -1L);
        //        internal Context compareContext => 
        internal long lastChange => (long)(mem[LastChange] ?? 0L);// compareContext?.db.loadpos ?? 0L;
        /// <summary>
        /// Sensitive if it contains a sensitive type
        /// </summary>
        internal bool sensitive => (bool)(mem[Sensitive] ?? false);
        internal Level classification => (Level)mem[Classification] ?? Level.D;
        internal BTree<long, ObInfo> infos =>
    (BTree<long, ObInfo>)mem[Infos] ?? BTree<long, ObInfo>.Empty;
        internal long domain => (long)(mem[_Domain] ?? -1L);
        internal long from => (long)(mem[_From] ?? -1L);
        /// <summary>
        /// For compiled code - triggers and Procedures
        /// </summary>
        internal Framing framing =>
            (Framing)mem[_Framing] ?? Framing.Empty;
        internal Ident id => (Ident)mem[_Ident];
        /// <summary>
        /// This list does not include indexes/columns/rows for tables
        /// or other obvious structural dependencies
        /// </summary>
        internal CTree<long, bool> dependents =>
            (CTree<long, bool>)mem[Dependents] ?? CTree<long, bool>.Empty;
        internal int depth => (int)(mem[_Depth] ?? 1);
        /// <summary>
        /// Constructor
        /// </summary>
        protected DBObject(long dp, BTree<long, object> m) : base(m)
        {
            defpos = dp;
        }
        protected DBObject(long pp, long dp, long dr, BTree<long, object> m = null)
            : this(dp, (m ?? BTree<long, object>.Empty) + (LastChange, pp) + (Definer, dr))
        { }
        public static DBObject operator +(DBObject ob, (long, object) x)
        {
            return (DBObject)ob.New(ob.mem + x);
        }
        /// <summary>
        /// Used for shared RowSet and RowSets to create new copies 
        /// when we want to modify a property (e.g. adding a filter).
        /// As far as I can see this is not required for SqlValues or Executables.
        /// </summary>
        /// <param name="cx"></param>
        /// <param name="m"></param>
        /// <returns></returns>
        internal virtual DBObject New(Context cx, BTree<long, object> m)
        {
            var r = (DBObject)New(mem+m);
            cx.Add(r);
            return r;
        }
        internal virtual (DBObject,Ident) _Lookup(long lp, Context cx,string nm, Ident n)
        {
            return (this,n);
        }
        internal static int _Max(params int[] x)
        {
            var r = 0;
            for (var i = 0; i < x.Length; i++)
                if (x[i] > r)
                    r = x[i];
            return r;
        }
        internal static BTree<long, object> _Deps(Context cx,CList<long> vs,DBObject ob=null)
        {
            var r = ob?.mem??BTree<long, object>.Empty;
            var os = ob?.dependents ?? CTree<long, bool>.Empty;
            var d = ob?.depth ??1;
            for (var b = vs?.First(); b != null; b = b.Next())
            {
                var p = b.value();
                if (cx._Ob(p) is DBObject o)
                {
                    d = Math.Max(d, o.depth + 1);
                    os += (p, true);
                }
            }
            return r + (Dependents,os) + (_Depth,d);
        }
        internal static BTree<long, object> _Deps(BList<SqlValue> vs,DBObject ob=null)
        {
            var r = ob?.mem?? BTree<long,object>.Empty;
            var os = ob?.dependents??CTree<long, bool>.Empty;
            var d = ob?.depth ??1;
            for (var b = vs?.First(); b != null; b = b.Next())
            {
                var o = b.value();
                d = Math.Max(d, o.depth + 1);
                os += (o.defpos, true);
            }
            return r + (Dependents, os) + (_Depth, d);
        }
        internal static BTree<long, object> _Deps(int d,params DBObject[] vs)
        {
            var r = BTree<long, object>.Empty;
            var os = CTree<long, bool>.Empty;
            foreach (var o in vs)
                if (o != null)
                {
                    d = Math.Max(d, o.depth + 1);
                    r += (o.defpos, true);
                }
            return r + (Dependents, os) + (_Depth, d);
        }
        /// <summary>
        /// Check to see if the current role has the given privilege on this (except Admin)
        /// For ADMIN and classified objects we check the current user has this privilege
        /// </summary>
        /// <param name="priv">The privilege in question</param>
        /// <returns>the current role if it has this privilege</returns>
        public virtual bool Denied(Context cx, Grant.Privilege priv)
        {
            var tr = cx.tr;
            if (tr == null)
                return false;
            if (defpos > Transaction.TransPos)
                return false;
            var oi = tr.role.infos[defpos];
            return (oi != null) && (oi.priv & priv) == 0;
        }
        internal virtual CTree<long, bool> Needs(Context cx)
        {
            if (cx.obs[domain] is Domain dm)
                return dm.Needs(cx);
            return CTree<long, bool>.Empty;
        }
        internal virtual CTree<long, bool> _Rdc(Context cx)
        {
            return CTree<long,bool>.Empty;
        }
        internal virtual ObInfo Inf(Context cx)
        {
            return infos[cx.role.defpos];
        }
        internal virtual CTree<long, bool> Needs(Context context, long rs)
        {
            return CTree<long, bool>.Empty;
        }
        internal virtual bool LocallyConstant(Context cx,RowSet rs)
        {
            return false;
        }
        /// <summary>
        /// This one is used mainly in commit to transaction log,
        /// blindly changes the defpos
        /// </summary>
        /// <param name="dp"></param>
        /// <returns></returns>
        internal abstract DBObject Relocate(long dp);
        /// <summary>
        /// When a Physical commits to transaction log, this routine
        /// helps to relocate the associated compiled objects
        /// </summary>
        /// <param name="wr"></param>
        /// <returns></returns>
        internal override Basis _Relocate(Context cx)
        {
            var r = Relocate(cx.Fix(defpos));
            var dm = cx.Fix(domain);
            if (dm != domain)
                r += (_Domain, dm);
            var df = cx.Fix(definer);
            if (df != definer)
                r += (Definer, df);
            var ds = CTree<long, bool>.Empty;
            for (var b = dependents.First(); b != null; b = b.Next())
                ds += (cx.Fix(b.key()), true);
            if (ds != dependents)
                r += (Dependents, ds);
            cx.Add(r);
            return r;
        }
        /// <summary>
        /// Adjust compiled DBObject (calls _Relocate)
        /// </summary>
        /// <param name="wr"></param>
        /// <returns></returns>
        internal DBObject Relocate(Context cx)
        {
            if (defpos < 0)
            {
                var dm = this as Domain ?? throw new PEException("PE688");
                return (Domain)dm._Relocate(cx);
            }
            if (cx.done.Contains(defpos))
                return cx.done[defpos];
            var r = (DBObject)_Relocate(cx);
            cx.Add(r);
            cx.done += (defpos, r);
            return r;
        }
        internal override Basis Fix(Context cx)
        {
            var r = (DBObject)_Fix(cx);
            if (defpos!=-1L)
                cx.Add(r);
            return r;
        }
        internal CTree<long, TypedValue> Frame(CTree<long, TypedValue> vs)
        {
            var map = CTree<long, long>.Empty;
            for (var b = framing.obs.First(); b != null; b = b.Next())
                if (b.value() is SqlCopy sc)
                    map += (sc.copyFrom, sc.defpos);
            var r = CTree<long, TypedValue>.Empty;
            for (var b = vs.First(); b != null; b = b.Next())
                if (map.Contains(b.key()))
                    r += (map[b.key()], b.value());
            return r;
        }
        /// <summary>
        /// Fix does the work of relocation for sharing - see Compiled.Relocate(cx)
        /// </summary>
        /// <param name="cx"></param>
        /// <returns></returns>
        internal override Basis _Fix(Context cx)
        {
            var r = this;
            var np = cx.Fix(defpos);
            if (np != defpos)
            {
                r = cx.obs[np];
                if (r == null || r is SqlNull)
                    r = cx._Add(Relocate(np));
            }
            var dm = cx.Fix(domain);
            if (dm != domain)
                r += (_Domain, dm);
            var fm = cx.Fix(from);
            if (fm != from)
                r += (_From, fm);
            var nd = cx.Fix(definer);
            if (definer != nd)
                r += (Definer, nd);
            var ds = cx.FixTlb(dependents);
            if (ds != dependents)
                r += (Dependents, ds);
            return r;
        }
        /// <summary>
        /// Some DBObjects are modified when metadata is defined
        /// </summary>
        /// <param name="d"></param>
        /// <param name="pm"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        internal virtual void Add(Context cx,PMetadata pm, long p)
        {
            var oi = infos[cx.role.defpos];
            cx.db += (this + (Infos, infos + (cx.role.defpos, oi + (ObInfo._Metadata, pm.detail)))
                + (LastChange,pm.ppos),p);
        }
        internal virtual BTree<long,SystemFilter> SysFilter(Context cx,BTree<long,SystemFilter> sf)
        {
            return sf;
        }
        internal virtual int Depth(params DBObject[] obs)
        {
            int de = depth;
            foreach (var ob in obs)
                if (ob.depth >= de)
                    de = ob.depth+1;
            return de;
        }
        internal virtual CTree<long,bool> Operands(Context cx)
        {
            return CTree<long, bool>.Empty;
        }
        internal virtual DBObject AddTrigger(Trigger tg)
        {
            return this;
        }
        /// <summary>
        /// Drop anything that needs this, directly or indirectly,
        /// and then drop this.
        /// Called by Drop for Database on Commit and Load
        /// </summary>
        /// <param name="d"></param>
        /// <param name="nd"></param>
        /// <returns></returns>
        internal virtual void Cascade(Context cx, Drop.DropAction a=0,
            BTree<long,TypedValue>u=null)
        {
            for (var b = ((Transaction)cx.db).physicals.First(); b != null; b = b.Next())
                if (b.value() is Drop dr && dr.delpos == defpos)
                    return;
            for (var b = dependents?.First(); b != null; b = b.Next())
                if (cx.db.objects[b.key()] is DBObject ob)
                {
                    if (a == 0)
                    {
                        if (!(this is Table tb && cx.db.objects[b.key()] is TableColumn tc
                            && tb.defpos == tc.tabledefpos))
                        {
                            throw new DBException("23001",
                                GetType().Name + " " + Uid(defpos), ob.GetType().Name + " " + Uid(b.key()));
                        }
                    }
                    ob.Cascade(cx, a, u);
                }
            cx.Add(new Drop1(defpos, a, cx.tr.nextPos, cx));
        }
        /// <summary>
        /// Execute an Insert operation for a Table, View, RestView.
        /// The new or existing Rowsets may be explicit or in the physical database.
        /// Deal with triggers.
        /// </summary>
        /// <param name="f">A query</param>
        /// <param name="prov">The provenance string</param>
        /// <param name="cl">The classification sought</param>
        internal virtual BTree<long, TargetActivation> Insert(Context cx, RowSet ts, CList<long> rt)
        {
            return BTree<long, TargetActivation>.Empty;
        }
        internal virtual BTree<long, TargetActivation> Delete(Context cx,RowSet fm)
        {
            return BTree<long, TargetActivation>.Empty;
        }
        internal virtual BTree<long, TargetActivation> Update(Context cx, RowSet fm)
        {
            return BTree<long, TargetActivation>.Empty;
        }
        internal virtual Database Drop(Database d, Database nd,long p)
        {
            return nd - defpos;
        }
        internal virtual Database DropCheck(long ck,Database nd,long p)
        {
            throw new NotImplementedException();
        }
        internal virtual TypedValue _Default()
        {
            return TNull.Value;
        }
        /// <summary>
        /// Discover if any call found on routine defpos
        /// </summary>
        /// <param name="defpos"></param>
        /// <param name="tr"></param>
        internal virtual bool Calls(long defpos,Context cx)
        {
            return false;
        }
        internal static bool Calls(BList<DBObject> vs, long defpos, Context cx)
        {
            for (var b = vs?.First(); b != null; b = b.Next())
                if (b.value().Calls(defpos, cx))
                    return true;
            return false;
        }
        internal virtual void Modify(Context cx, Modify m, long p)
        {
            cx.db += ((Method)cx.obs[m.proc], p);
        }
        internal virtual DBObject _Replace(Context cx, DBObject so, DBObject sv)
        {
            return this;
        }
        internal DBObject Replace(Context cx,DBObject was,DBObject now)
        {
            var ldpos = cx.db.loadpos;
            for (var cc = cx.next; cc != null; cc = cc.next)
                ldpos = cc.db.loadpos;
            if (defpos < ldpos)
                return this;
            var r = _Replace(cx, was, now);
            if (r != this && dependents.Contains(was.defpos) && (now.depth + 1) > depth)
            {
                r += (_Depth, now.depth + 1);
                cx.done += (r.defpos, r);
            }
            for (var b = dependents.First(); b != null; b = b.Next())
                if (cx.done[b.key()] is DBObject d && d.depth >= r.depth)
                {
                    r += (_Depth, d.depth + 1);
                    cx.done += (r.defpos, r);
                } 
            return r;
        }
        internal virtual object Build(Context _cx, RowSet rs)
        {
            return null;
        }
        internal virtual void _Add(Context cx)
        {
            cx.obs += (defpos, this);
            if (domain >= 0)
                cx.Add(cx._Dom(this));
        }
        /// <summary>
        /// Add a new column to the query, and update the row type
        /// (Needed for alter)
        /// </summary>
        /// <param name="cx"></param>
        /// <param name="n"></param>
        /// <returns></returns>
        internal virtual DBObject Add(Context cx, SqlValue v)
        {
            if (v == null)
                return this;
            var dm = (Domain)cx.obs[domain];
            dm = cx._Dom(dm, v.defpos, cx._Dom(v));
            var r = (DBObject)New(_Deps(depth,v)  + (_Domain,dm.defpos));
            return r;
        }
        internal virtual DBObject Remove(Context cx, SqlValue v)
        {
            if (v == null)
                return this;
            var rt = CList<long>.Empty;
            var rp = CTree<long, Domain>.Empty;
            var ch = false;
            var dm = cx._Dom(this);
            var rb = dm.representation.First();
            for (var b = dm.rowType?.First(); b != null && rb != null; b = b.Next(), rb = rb.Next())
                if (b.value() == v.defpos)
                    ch = true;
                else
                {
                    rp += (rb.key(), rb.value());
                    rt += b.value();
                }
            return ch ?
                New(cx, mem + (_Domain, cx._Dom(domain, rt).defpos) + (Dependents, dependents - v.defpos))
                : this;
        }
        internal virtual void _ReadConstraint(Context cx,TableRowSet.TableCursor cu)
        { }
        internal virtual DBObject Orders(Context cx, CList<long> ord)
        {
            return this;
        }
        public static bool Eval(CTree<long, bool> svs, Context cx)
        {
            for (var b = svs?.First(); b != null; b = b.Next())
                if (cx.obs[b.key()].Eval(cx) != TBool.True)
                    return false;
            return true;
        }
        internal virtual void Set(Context cx, TypedValue v)
        {
            cx.values += (defpos, v);
        }
        /// <summary>
        /// Replace TypedValues that are QParams with actuals
        /// </summary>
        /// <param name="cx"></param>
        /// <returns></returns>
        internal virtual DBObject QParams(Context cx)
        {
            return this;
        }
        /// <summary>
        /// If the value contains aggregates we need to accumulate them
        /// </summary>
        internal virtual BTree<long,Register> StartCounter(Context _cx, RowSet rs,BTree<long,Register> tg)
        {
            return tg;
        }
        /// <summary>
        /// If the value contains aggregates we need to accumulate them. 
        /// Carefully watch out for common subexpressions, and only AddIn once!
        /// </summary>
        internal virtual BTree<long, Register> AddIn(Context _cx, Cursor rb, BTree<long, Register> tg) 
        {
            return tg;
        }
        internal virtual DBObject TypeOf(long lp,Context cx,TypedValue v)
        {
            throw new System.NotImplementedException();
        }
        internal virtual TypedValue Eval(Context cx)
        {
            return cx.values[defpos];
        }
        internal virtual CTree<long,TypedValue> Add(Context cx,CTree<long,TypedValue> ma,
            Table tb=null)
        {
            return ma;
        }
        /// <summary>
        /// Check constraints can be added to Domains, TableColumns and Tables
        /// </summary>
        /// <param name="ck"></param>
        /// <param name="db"></param>
        /// <returns></returns>
        internal virtual DBObject Add(Check ck,Database db)
        {
            throw new PEException("PE481");
        }
        internal virtual DBObject AddProperty(Check ck, Database db)
        {
            throw new PEException("PE481");
        }
        internal virtual RowSet RowSets(Ident id,Context cx, Domain q, long fm, 
            Grant.Privilege pr=Grant.Privilege.Select,string a=null)
        {
            return new TrivialRowSet(id.iix.dp, cx, new TRow(q, cx.values),pr,a);
        }
        /// <summary>
        /// Creates new instances of objects in framing lists.
        /// Label instances of framing objects with instanceOf (see Context.Add) 
        /// </summary>
        /// <param name="cx"></param>
        /// <returns></returns>
        internal virtual DBObject Instance(long lp,Context cx,BList<Ident>cs=null)
        {
            cx.Add(framing);
            return this;
        }
        /// <summary>
        /// Implementation of the Role$Class table: Produce a C# class corresponding to a Table or View
        /// </summary>
        /// <param name="from">A query</param>
        /// <param name="_enu">An enumerator for the set of database objects</param>
        /// <returns>A row for the Role$Class table</returns>
        internal virtual TRow RoleClassValue(Context cx,DBObject from,
            ABookmark<long, object> _enu)
        {
            return null;
        } 
        /// <summary>
        /// Implementation of the Role$Java table: Produce a Java class corresponding to a Table or View
        /// </summary>
        /// <param name="from">A query</param>
        /// <param name="_enu">An enumerator for the set of database objects</param>
        /// <returns>A row for the Role$Class table</returns>
        internal virtual TRow RoleJavaValue(Context cx, DBObject from, ABookmark<long, object> _enu)
        {
            return null;
        }
        /// <summary>
        /// Implementation of the Role$Python table: Produce a Python class corresponding to a Table or View
        /// </summary>
        /// <param name="from">A query</param>
        /// <param name="_enu">An enumerator for the set of database objects</param>
        /// <returns>A row for the Role$Class table</returns>
        internal virtual TRow RolePythonValue(Context cx, DBObject from, ABookmark<long, object> _enu)
        {
            return null;
        }
         /// <summary>
        /// Issues here: This object may not have been committed yet
        /// We only want to record audits in the PhysBase for committed obs
        /// </summary>
        /// <param name="tr"></param>
        /// <param name="m"></param>
        internal void Audit(Context cx, RowSet rs)
        {
            if (cx.tr == null || cx.db.user.defpos == cx.db.owner)
                return;
            var tb = this as Table;
            if (defpos >= Transaction.TransPos)
                return;
            var mac = (tb?.classification.maxLevel ?? 0) > 0&&
                tb?.enforcement.HasFlag(Grant.Privilege.Select) ==true
                && cx.db._user!=cx.db.owner;
            if (!(mac || sensitive))
                return;
            if (!sensitive)
            {
                var found = false;
                for (var b = rs.First(cx); (!found) && b != null; b = b.Next(cx))
                    if (b[Classification]is TLevel lv && lv.Val() is Level vl 
                        && vl.maxLevel > 0)
                        found = true;
                if (!found)
                    return;
            }
            var match = CTree<long, string>.Empty;
            for (var b = rs.matches?.First(); b != null; b = b.Next())
                match += (b.key(), b.value()?.ToString() ?? "null");
            var a = new Audit(cx.tr.user, defpos, match, DateTime.Now.Ticks, cx.db.nextPos, cx);
            if (cx.auds.Contains(a))
                return;
            cx.auds += (a, true);
            cx.tr.Audit(a, cx); // write it to the file immediately
        }
        internal static string Uid(long u)
        {
            if (u >= Transaction.HeapStart)
                return "%" + (u - Transaction.HeapStart);
            if (u >= Transaction.Executables)
                return "`" + (u - Transaction.Executables);
            if (u >= Transaction.Analysing)
                return "#" + (u - Transaction.Analysing);
            if (u >= Transaction.TransPos)
                return "!" + (u - Transaction.TransPos); 
            if (u == -1)
                return "_";
            return "" + u;
        }
        internal virtual string NameFor(Context cx)
        {
            var ob = cx._Ob(defpos);
            if (ob.alias is string s)
                return s;
            var ci = ob.infos[cx.role.defpos] ?? ob.infos[definer] ??
                ob.infos[Database.Guest] ?? throw new DBException("42105");
            return ci.name;
        }
        internal virtual void Note(Context cx,StringBuilder sb)
        {  }
        public override string ToString()
        {
            var sb = new StringBuilder(base.ToString());
            if (id != null)
            {
                sb.Append(' '); sb.Append(id);
            }
            sb.Append(' '); sb.Append(Uid(defpos));
            if (domain < -1L)
            {
                sb.Append(' ');
                if (Context._system.db.objects[domain] is Domain dm)
                    sb.Append(dm.kind);
                else
                    sb.Append(Uid(domain));
            }
            else if (domain>0)
            { sb.Append(" Domain "); sb.Append(Uid(domain)); }
            if (mem.Contains(Definer)) { sb.Append(" Definer="); sb.Append(Uid(definer)); }
            if (mem.Contains(Classification)) { sb.Append(" Classification="); sb.Append(classification); }
            if (mem.Contains(LastChange)) { sb.Append(" LastChange="); sb.Append(Uid(lastChange)); }
            if (sensitive) sb.Append(" Sensitive"); 
            return sb.ToString();
        }
    }
    internal class ObTree: BTree<long, DBObject>
    {
        public new readonly static ObTree Empty = new ObTree();
        internal ObTree() : base(null) { }
        internal ObTree(long k, DBObject v) : base(k, v) { }
        internal ObTree(Bucket<long, DBObject> b) : base(b) { }
        public static ObTree operator +(ObTree tree, (long, DBObject) v)
        {
            return (ObTree)tree.Add(v.Item1, v.Item2);
        }
        public static ObTree operator +(ObTree tree, BTree<long, DBObject> a)
        {
            return (ObTree) tree.Add(a);
        }
        public static ObTree operator -(ObTree tree, long k)
        {
            return (ObTree)tree?.Remove(k);
        }
        protected override ATree<long, DBObject> Add(long k, DBObject v)
        {
            if (Contains(k))
                return new ObTree(root.Update(this, k, v));
            return Insert(k, v);
        }
        public override ATree<long, DBObject> Add(ATree<long, DBObject> a)
        {
            var tree = this;
            for (var b = a?.First(); b != null; b = b.Next())
                tree = (ObTree)tree.Add(b.key(), b.value());
            return tree;
        }
        protected override ATree<long, DBObject> Insert(long k, DBObject v) // this does not contain k
        {
            if (root == null || root.total == 0)  // empty BTree
                return new ObTree(k, v);
            if (root.count == Size)
                return new ObTree(root.Split()).Add(k, v);
            return new ObTree(root.Add(this, k, v));
        }
        protected override ATree<long, DBObject> Update(long k, DBObject v) // this Contains k
        {
            if (!Contains(k))
                throw new Exception("PE01");
            return new ObTree(root.Update(this, k, v));
        }

        protected override ATree<long, DBObject> Remove(long k)
        {
            if (!Contains(k))
                return this;
            if (root.total == 1) // empty index
                return Empty;
            // note: we allow root to have 1 entry
            return new ObTree(root.Remove(this, k));
        }
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            var cm = "(";
            for (var b = First(); b != null; b = b.Next())
            {
                sb.Append(cm); cm = ",\n  ";
                sb.Append(DBObject.Uid(b.key())); sb.Append("=");
                sb.Append(b.value());
            }
            if (cm != "(")
                sb.Append(")");
            return sb.ToString();
        }
    }
    internal class ForwardReference : DBObject
    {
        internal CTree<long,bool> subs => (CTree<long, bool>)mem[Domain.RowType]??CTree<long, bool>.Empty;
        /// <summary>
        /// A forward reference non-terminal.
        /// Non-terminal, will always have suggested columns
        /// </summary>
        /// <param name="nm">Name</param>
        /// <param name="cx">The context</param>
        /// <param name="lp">Lexical position: stored in LastChange</param>
        /// <param name="dr">Definer</param>
        /// <param name="m">Other properties, e.g. domain, depth</param>
        public ForwardReference(Ident n, Context cx, long dr, BTree<long, object> m = null) 
            : base(n.iix.lp, n.iix.dp, dr, cx.Name(n,m))
        {
            cx.Add(this);
            cx.undefined += (defpos,true);
        }
        protected ForwardReference(long dp, BTree<long, object> m)
            : base(dp, m)
        { }
        internal override Basis New(BTree<long, object> m)
        {
            return new ForwardReference(defpos,m);
        }
        public static ForwardReference operator+(ForwardReference fr,(long,object)x)
        {
            return (ForwardReference)fr.New(fr.mem + x);
        }
        internal override Basis _Relocate(Context cx)
        {
            var r = (ForwardReference)base._Relocate(cx);
            r += (_Ident, id.Fix(cx));
            return r;
        }
        internal override DBObject Relocate(long dp)
        {
            return new ForwardReference(dp,mem);
        }
    }
}
