using System;
using System.Text;
using System.Collections.Generic;
using Pyrrho.Level2;
using Pyrrho.Common;
using Pyrrho.Level4;
using System.Runtime.CompilerServices;
using static Pyrrho.Level4.RowSet;
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
    /// When a Table is accessed
    /// any role with select access to the table will be able to retrieve rows subject 
    /// to security clearance and classification. Which columns are accessible also depends
    /// on privileges (but columns are not subject to classification).
    /// // shareable as of 26 April 2021
    /// </summary>
    internal class Table : DBObject
    {
        internal const long
            ApplicationPS = -262, // long PeriodSpecification
            Enforcement = -263, // Grant.Privilege (T)
            Indexes = -264, // CTree<CList<long>,CTree<long,bool>> SqlValue,Index
            KeyCols = -320, // CTree<long,bool> TableColumn (over all indexes)
            LastData = -258, // long
            RefIndexes = -250, // CTree<long,CTree<CList<long>,CList<long>>> referencing Table,referencing TableColumns,referenced TableColumns
            SystemPS = -265, //long (system-period specification)
            TableChecks = -266, // CTree<long,bool> Check
            TableCols = -332, // CTree<long,Domain> TableColumn
            TableRows = -181, // BTree<long,TableRow>
            Triggers = -267; // BTree<PTrigger.TrigType,BTree<long,bool>> (T) 
        /// <summary>
        /// The rows of the table with the latest version for each
        /// </summary>
		public BTree<long, TableRow> tableRows => 
            (BTree<long,TableRow>)mem[TableRows]??BTree<long,TableRow>.Empty;
        public CTree<CList<long>, CTree<long,bool>> indexes => 
            (CTree<CList<long>,CTree<long,bool>>)mem[Indexes]??CTree<CList<long>,CTree<long,bool>>.Empty;
        public CTree<long, bool> keyCols =>
            (CTree<long, bool>)mem[KeyCols] ?? CTree<long, bool>.Empty;
        internal CTree<long, Domain> tableCols =>
            (CTree<long, Domain>)mem[TableCols] ?? CTree<long, Domain>.Empty;
        /// <summary>
        /// Enforcement of clearance rules
        /// </summary>
        internal Grant.Privilege enforcement => (Grant.Privilege)(mem[Enforcement]??0);
        internal long applicationPS => (long)(mem[ApplicationPS] ?? -1L);
        internal string iri => (string)mem[Domain.Iri];
        internal long systemPS => (long)(mem[SystemPS] ?? -1L);
        internal CTree<long,CTree<CList<long>,CList<long>>> rindexes =>
            (CTree<long,CTree<CList<long>,CList<long>>>)mem[RefIndexes] 
            ?? CTree<long,CTree<CList<long>,CList<long>>>.Empty;
        internal CTree<long, bool> tableChecks => 
            (CTree<long, bool>)mem[TableChecks]??CTree<long,bool>.Empty;
        internal CTree<PTrigger.TrigType, CTree<long,bool>> triggers =>
            (CTree<PTrigger.TrigType, CTree<long, bool>>)mem[Triggers]
            ??CTree<PTrigger.TrigType, CTree<long, bool>>.Empty;
        internal virtual long lastData => (long)(mem[LastData] ?? 0L);
        // definer's privileges for a new table
        static Grant.Privilege priv = Grant.Privilege.Owner | Grant.Privilege.Insert | Grant.Privilege.Select |
                Grant.Privilege.Update | Grant.Privilege.Delete | Grant.Privilege.References |
                Grant.Privilege.GrantDelete | Grant.Privilege.GrantSelect |
                Grant.Privilege.GrantInsert | Grant.Privilege.GrantReferences |
                Grant.Privilege.Usage | Grant.Privilege.GrantUsage |
                Grant.Privilege.Trigger | Grant.Privilege.GrantTrigger |
                Grant.Privilege.Metadata | Grant.Privilege.GrantMetadata;
        /// <summary>
        /// Constructor: a new empty table
        /// </summary>
        internal Table(PTable pt,Role ro,Context cx) :base(pt.ppos, BTree<long,object>.Empty
            +(Definer,ro.defpos)
            +(Infos,new BTree<long,ObInfo>(ro.defpos,new ObInfo(pt.name,priv)))
            +(_Framing,new Framing(cx,pt.nst))
            +(Indexes,CTree<CList<long>,CTree<long,bool>>.Empty) + (LastChange, pt.ppos)
            + (_Domain,pt.dataType.defpos)+(LastChange,pt.ppos)
            +(Triggers, CTree<PTrigger.TrigType, CTree<long, bool>>.Empty)
            +(Enforcement,(Grant.Privilege)15)) //read|insert|update|delete
        { }
        protected Table(long dp, BTree<long, object> m) : base(dp, m) { }
        public static Table operator+(Table tb,(Context,DBObject)x) // tc can be SqlValue for Type def
        {
            var (cx, tc) = x;
            var cd = cx._Dom(tc);
            var ts = tb.tableCols + (tc.defpos, cd);
            var m = tb.mem + _Deps(tb.depth,tc) + (TableCols, ts);
            if (tc.sensitive)
                m += (Sensitive, true);
            return (Table)tb.New(m);
        }
        public static Table operator-(Table tb,long p)
        {
            return (Table)tb.New(tb.mem + (TableRows,tb.tableRows-p));
        }
        /// <summary>
        /// Add a new or updated row, indexes already fixed.
        /// </summary>
        /// <param name="t"></param>
        /// <param name="rw"></param>
        /// <returns></returns>
        public static Table operator +(Table t, TableRow rw)
        {
            var se = t.sensitive || rw.classification!=Level.D;
            return (Table)t.New(t.mem + (TableRows,t.tableRows+(rw.defpos,rw)) 
                + (Sensitive,se));
        }
        public static Table operator+(Table tb,(long,object)v)
        {
            return (Table)tb.New(tb.mem + v);
        }

        internal virtual ObInfo _ObInfo(long ppos, string name, Grant.Privilege priv)
        {
            return new ObInfo(name,priv);
        }
        internal override DBObject Add(Check ck, Database db)
        {
            return new Table(defpos,mem+(TableChecks,tableChecks+(ck.defpos,true)));
        }
        internal override void _Add(Context cx)
        {
            base._Add(cx);
            for (var b = tableCols.First(); b != null; b = b.Next())
            {
         /*       var p = b.value();
                if (p!=Domain.Content)
                    p.Instance(cx); */
                cx.Add((DBObject)cx.db.objects[b.key()]);
            }
        }
        internal override DBObject AddTrigger(Trigger tg)
        {
            var tb = this;
            var ts = triggers[tg.tgType] ?? CTree<long, bool>.Empty;
            return tb + (Triggers, triggers+(tg.tgType, ts + (tg.defpos, true)));
        }
        internal override Basis New(BTree<long, object> m)
        {
            return new Table(defpos,m);
        }
        internal override DBObject Relocate(long dp)
        {
            return new Table(dp, mem);
        }
        internal override Basis _Relocate(Context cx)
        {
            var r = (Table)base._Relocate(cx);
            if (applicationPS>=0)
                r += (ApplicationPS, cx.Fix(applicationPS));
            r += (Indexes, cx.FixTLTllb(indexes));
            r += (TableCols, cx.FixTlD(tableCols));
            if (systemPS >= 0)
                r += (SystemPS, cx.Fix(systemPS));
            r += (TableChecks, cx.FixTlb(tableChecks));
            r += (Triggers, cx.FixTTElb(triggers));
            return r;
        }
        internal override Basis _Fix(Context cx)
        {
            var r = (Table) base._Fix(cx);
            var na = cx.Fix(applicationPS);
            if (na!=applicationPS)
                r += (ApplicationPS, na);
            var ni = cx.FixTLTllb(indexes);
            if (ni!=indexes)
                r += (Indexes, ni);
            var tc = cx.FixTlD(tableCols);
            if (tc!=tableCols)
                r += (TableCols, tc);
            var ns = cx.Fix(systemPS);
            if (ns!=systemPS)
                r += (SystemPS, ns);
            var nc = cx.FixTlb(tableChecks);
            if (nc!=tableChecks)
                r += (TableChecks, nc);
            var nt = cx.FixTTElb(triggers);
            if (nt!=triggers)
                r += (Triggers, nt);
            return r;
        }
        internal override DBObject _Replace(Context cx, DBObject so, DBObject sv)
        {
            if (cx.done.Contains(defpos))
                return cx.done[defpos];
            var r = base._Replace(cx,so,sv);
            var dm = cx.ObReplace(domain, so, sv);
            if (dm != domain)
                r += (_Domain, dm);
            if (r!=this)
                r = (Table)New(cx,r.mem);
            cx.done += (defpos, r);
            return r;
        }
        internal override void Cascade(Context cx,
            Drop.DropAction a = 0, BTree<long, TypedValue> u = null)
        {
            base.Cascade(cx, a, u);
            for (var b = indexes.First(); b != null; b = b.Next())
                for (var c=b.value().First();c!=null;c=c.Next())
                ((Index)cx.db.objects[c.key()])?.Cascade(cx,a,u);
            for (var b = cx.role.dbobjects.First(); b != null; b = b.Next())
                if (cx.db.objects[b.value()] is Table tb)
                    for (var c = tb.indexes.First(); c != null; c = c.Next())
                        for (var d=c.value().First(); d!=null; d=d.Next())
                        if (((Index)cx.db.objects[d.key()])?.reftabledefpos == defpos)
                            tb.Cascade(cx,a,u);
        }
        internal override Database Drop(Database d, Database nd, long p)
        {
            for (var b = d.roles.First(); b != null; b = b.Next())
            {
                var q = b.value();
                var ro = (Role)d.objects[q];
                if (infos[q] is ObInfo oi)
                {
                    ro += (Role.DBObjects, ro.dbobjects - oi.name);
                    nd += (ro, p);
                }
            }
            return base.Drop(d, nd, p);
        }
        internal override Database DropCheck(long ck, Database nd, long p)
        {
            return nd + (this + (TableChecks, tableChecks - ck),p);
        }
        internal virtual Index FindPrimaryIndex(Context cx)
        {
            for (var b = indexes.First(); b != null; b = b.Next())
                for (var c = b.value().First(); c != null; c = c.Next())
                {
                    var ix = (Index)cx.db.objects[c.key()];
                    if (ix.flags.HasFlag(PIndex.ConstraintType.PrimaryKey))
                         return ix;
                }
            return null;
        }
        internal Index[] FindIndex(Database db, CList<long> key, 
            PIndex.ConstraintType fl=(PIndex.ConstraintType.PrimaryKey | PIndex.ConstraintType.Unique))
        {
            var r = BList<Index>.Empty;
            for (var b = indexes[key]?.First(); b != null; b = b.Next())
            if (db.objects[b.key()] is Index x && (x.flags&fl)!=0)
                    r += x;
            return (r==BList<Index>.Empty)?null:r.ToArray();
        }
        internal override RowSet RowSets(Ident id, Context cx, Domain q, long fm, 
            Grant.Privilege pr=Grant.Privilege.Select,string a=null)
        {
            cx.Add(this);
            var m = BTree<long, object>.Empty + (_From, fm) + (_Alias, a) + (_Ident,id);
            var rowSet = (RowSet)cx._Add(new TableRowSet(id.iix.dp, cx, defpos,m));
#if MANDATORYACCESSCONTROL
            Audit(cx, rowSet);
#endif
            return rowSet;
        }
        public override bool Denied(Context cx, Grant.Privilege priv)
        { 
            if (cx.db.user != null && enforcement.HasFlag(priv) &&
                !(cx.db.user.defpos == cx.db.owner
                    || cx.db.user.clearance.ClearanceAllows(classification)))
                return true;
            return base.Denied(cx, priv);
        }
        internal CTree<CList<long>, CTree<long,bool>> IIndexes(CTree<long, long> sim)
        {
            var xs = CTree<CList<long>, CTree<long, bool>>.Empty;
            for (var b = indexes.First(); b != null; b = b.Next())
            {
                var k = CList<long>.Empty;
                for (var c = b.key().First(); c != null; c = c.Next())
                    k += sim[c.value()];
                xs += (k, b.value());
            }
            return xs;
        }
        /// <summary>
        /// A readable version of the Table
        /// </summary>
        /// <returns>the string representation</returns>
        public override string ToString()
        {
            var sb = new StringBuilder(base.ToString());
            sb.Append(":"); sb.Append(Uid(domain));
            if (PyrrhoStart.VerboseMode && mem.Contains(Enforcement)) 
            { sb.Append(" Enforcement="); sb.Append(enforcement); }
            if (indexes.Count!=0) 
            { 
                sb.Append(" Indexes:(");
                var cm = "";
                for (var b=indexes.First();b!=null;b=b.Next())
                {
                    sb.Append(cm);cm = ";";
                    var cn = "(";
                    for (var c=b.key().First();c!=null;c=c.Next())
                    {
                        sb.Append(cn);cn = ",";
                        sb.Append(Uid(c.value()));
                    }
                    sb.Append(")"); cn = "";
                    for (var c=b.value().First();c!=null;c=c.Next())
                    {
                        sb.Append(cn); cn = ",";
                        sb.Append(Uid(c.key()));
                    }
                }
                sb.Append(")");
                sb.Append(" KeyCols: "); sb.Append(keyCols);
            }
            if (triggers.Count!=0) { sb.Append(" Triggers:"); sb.Append(triggers); }
            return sb.ToString();
        }
        internal string ToCamel(string s)
        {
            var sb = new StringBuilder();
            sb.Append(char.ToLower(s[0]));
            sb.Append(s.Substring(1));
            return sb.ToString();
        }
        /// <summary>
        /// Generate a row for the Role$Class table: includes a C# class definition,
        /// and computes navigation properties
        /// </summary>
        /// <param name="from">The query</param>
        /// <param name="_enu">The object enumerator</param>
        /// <returns></returns>
        internal override TRow RoleClassValue(Context cx, DBObject from, 
            ABookmark<long, object> _enu)
        {
            var ro = cx.db.role;
            var nm = NameFor(cx);
            var md = cx._Dom(this);
            var mi = infos[cx.role.defpos];
            var versioned = mi.metadata.Contains(Sqlx.ENTITY);
            var key = BuildKey(cx, out CList<long> keys);
            var fields = CTree<string, bool>.Empty;
            var types = CTree<string, string>.Empty;
            var sb = new StringBuilder("\r\nusing System;\r\nusing Pyrrho;\r\n");
            sb.Append("\r\n/// <summary>\r\n");
            sb.Append("/// Class " + nm + " from Database " + cx.db.name 
                + ", Role " + ro.name + "\r\n");
            if (mi.description != "")
                sb.Append("/// " + mi.description + "\r\n");
            for (var b = indexes.First(); b != null; b = b.Next())
                for (var c = b.value().First(); c != null; c = c.Next())
                    if (cx._Ob(c.key()) is Index x)
                        x.Note(cx, sb);
            for (var b = tableChecks.First(); b != null; b = b.Next())
                if (cx._Ob(b.key()) is Check ck)
                    ck.Note(cx, sb);
            sb.Append("/// </summary>\r\n");
            sb.Append("[Table("); sb.Append(defpos);  sb.Append(","); sb.Append(lastChange); sb.Append(")]\r\n");
            sb.Append("public class " + nm + (versioned ? " : Versioned" : "") + " {\r\n");
            for (var b = md.representation.First();b!=null;b=b.Next())
            {
                var p = b.key();
                var dt = b.value();
                var tn = ((dt.kind == Sqlx.TYPE) ? dt.name : dt.SystemType.Name) + "?"; // all fields nullable
                dt.FieldType(cx,sb);
                var ci = infos[cx.role.defpos];
                if (ci != null)
                {
                    fields += (ci.name, true);
                    for (var d = ci.metadata.First(); d != null; d = d.Next())
                        switch (d.key())
                        {
                            case Sqlx.X:
                            case Sqlx.Y:
                                sb.Append(" [" + d.key().ToString() + "]\r\n");
                                break;
                        }
                    if (ci.description?.Length > 1)
                        sb.Append("  // " + ci.description + "\r\n");
                    if (cx._Ob(p) is TableColumn tc)
                    {
                        for (var c = tc.constraints.First(); c != null; c = c.Next())
                            if (cx._Ob(c.key()) is Check ck)
                                ck.Note(cx, sb);
                        if (tc.generated is GenerationRule gr)
                            gr.Note(cx, sb);
                    }
                    for (var c=dt.constraints.First(); c != null; c = c.Next())
                        if (cx._Ob(c.key()) is DBObject ck)
                            ck.Note(cx, sb);
                }
                else
                   fields += (cx.obs[p].infos[cx.role.defpos].name, true); 
                if ((keys.Last()?.value()??-1L)==p && dt.kind==Sqlx.INTEGER)
                    sb.Append("  [AutoKey]\r\n");
                var cn = cx.NameFor(p);
                sb.Append("  public " + tn + " " + cn + ";\r\n");
                types += (cn, tn);
            }
            for (var b = indexes.First(); b != null; b = b.Next())
                for (var c = b.value().First(); c != null; c = c.Next())
                {
                    var x = (Index)(cx.obs[c.key()]??cx.db.objects[c.key()]);
                    if (x.flags.HasFlag(PIndex.ConstraintType.ForeignKey))
                    {
                        // many-one relationship
                        var sa = new StringBuilder();
                        var cm = "";
                        for (var d = b.key().First(); d != null; d = d.Next())
                        {
                            sa.Append(cm); cm = ",";
                            sa.Append(cx.NameFor(d.value()));
                        }
                        var rx = (Index)cx.db.objects[x.refindexdefpos];
                        var rt = cx._Ob(rx.tabledefpos).infos[cx.role.defpos];
                        if (!rt.metadata.Contains(Sqlx.ENTITY))
                            continue;
                        var rn = ToCamel(rt.name);
                        for (var i = 0; fields.Contains(rn); i++)
                            rn = ToCamel(rt.name) + i;
                        fields += (rn, true);
                        sb.Append("  public " + rt.name + " " + rn
                            + "=> conn.FindOne<" + rt.name + ">(" + sa.ToString() + ");\r\n");
                    }
                }
            for (var b = rindexes.First(); b != null; b = b.Next())
            {
                var rt = cx._Ob(b.key()).infos[cx.role.defpos];
                if (rt.metadata.Contains(Sqlx.ENTITY))
                {
                    var tb = (Table)cx.db.objects[b.key()];
                    for (var c = b.value().First(); c != null; c = c.Next())
                    {
                        var sa = new StringBuilder();
                        var cm = "(\"";
                        var rn = ToCamel(rt.name);
                        for (var i = 0; fields.Contains(rn); i++)
                            rn = ToCamel(rt.name) + i;
                        fields += (rn, true);
                        var x = tb.FindIndex(cx.db,c.key())?[0] ;
                        if (x!=null)
                        // one-one relationship
                        {
                            cm = "";
                            for (var bb = c.value().First(); bb != null; bb = bb.Next())
                            {
                                sa.Append(cm); cm = ",";
                                var vi = cx._Ob(bb.value()).infos[cx.role.defpos];
                                sa.Append(vi.name);
                            }
                            sb.Append("  public " + rt.name + " " + rn
                                + "s => conn.FindOne<" + rt.name + ">(" + sa.ToString() + ");\r\n");
                            continue;
                        } 
                        // one-many relationship
                        var rb = c.value().First();
                        for (var xb = c.key().First(); xb != null && rb != null; xb = xb.Next(), rb = rb.Next())
                        {
                            sa.Append(cm); cm = "),(\"";
                            sa.Append(cx.NameFor(xb.value())); sa.Append("\",");
                            sa.Append(cx.NameFor(rb.value()));
                        }
                        sa.Append(")");
                        sb.Append("  public " + rt.name + "[] " + rn
                            + "s => conn.FindWith<" + rt.name + ">(" + sa.ToString() + ");\r\n");
                    }
                } else //  e.g. this is Brand
                {
                    var pt = (Table)cx.db.objects[b.key()]; // auxiliary table e.g. BrandSupplier
                    for (var d = pt.indexes.First(); d != null; d = d.Next())
                        for (var e = d.value().First(); e != null; e = e.Next())
                        {
                            var px = (Index)cx.db.objects[e.key()];
                            if (px.reftabledefpos == defpos)
                                continue;
                            // many-many relationship 
                            var tb = (Table)cx.db.objects[px.reftabledefpos]; // e.g. Supplier
                            if (tb == null)
                                continue;
                            var ti = tb.infos[cx.role.defpos];
                            if (!ti.metadata.Contains(Sqlx.ENTITY))
                                continue;
                            var tx = tb.FindPrimaryIndex(cx);
                            var sk = new StringBuilder(); // e.g. Supplier primary key
                            var cm = "\\\"";
                            for (var c = tx.keys.First(); c != null; c = c.Next())
                            {
                                sk.Append(cm); cm = "\\\",\\\"";
                                var ci = cx._Ob(c.value()).infos[cx.role.defpos];
                                sk.Append(ci.name);
                            }
                            sk.Append("\\\"");
                            var sa = new StringBuilder(); // e.g. BrandSupplier.Brand = Brand
                            cm = "\\\"";
                            var rb = px.keys.First();
                            for (var xb = keys.First(); xb != null && rb != null;
                                xb = xb.Next(), rb = rb.Next())
                            {
                                sa.Append(cm); cm = "\\\" and \\\"";
                                sa.Append(cx.NameFor(xb.value())); sa.Append("\\\"=\\\"");
                                sa.Append(cx.NameFor(rb.value()));
                            }
                            sa.Append("\\\"");
                            var rn = ToCamel(rt.name);
                            for (var i = 0; fields.Contains(rn); i++)
                                rn = ToCamel(rt.name) + i;
                            fields += (rn, true);
                            sb.Append("  public " + ti.name + "[] " + rn
                                + "s => conn.FindIn<" + ti.name + ">(\"select "
                                + sk.ToString() + " from \\\"" + rt.name + "\\\" where "
                                + sa.ToString() + "\");\r\n");
                        }
                }
            }
            sb.Append("}\r\n");
            return new TRow(cx,cx._Dom(from),new TChar(md.name),new TChar(key),
                new TChar(sb.ToString()));
        } 
        /// <summary>
        /// Generate a row for the Role$Java table: includes a Java class definition
        /// </summary>
        /// <param name="from">The query</param>
        /// <param name="_enu">The object enumerator</param>
        /// <returns></returns>
        internal override TRow RoleJavaValue(Context cx, DBObject from, 
            ABookmark<long, object> _enu)
        {
            var md = cx._Dom(this);
            var mi = infos[cx.role.defpos];
            var versioned = true;
            var sb = new StringBuilder();
            sb.Append("/*\r\n * "); sb.Append(md.name); sb.Append(".java\r\n *\r\n * Created on ");
            sb.Append(DateTime.Now);
            sb.Append("\r\n * from Database " + cx.db.name + ", Role " 
                + cx.db.role.name + "r\n */");
            sb.Append("import org.pyrrhodb.*;\r\n");
            var key = BuildKey(cx,out CList<long> keys);
            sb.Append("\r\n@Schema("); sb.Append(from.lastChange); sb.Append(")");
            sb.Append("\r\n/**\r\n *\r\n * @author "); sb.Append(cx.db.user.name); sb.Append("\r\n */");
            if (mi.description != "")
                sb.Append("/* " + mi.description + "*/\r\n");
            sb.Append("public class " + md.name + ((versioned) ? " extends Versioned" : "") + " {\r\n");
            for(var b = md.rowType.First();b!=null;b=b.Next())
            {
                var p = b.key();
                var cd = tableCols[b.value()];
                var dt = cd;
                var tn = (dt.kind == Sqlx.TYPE) ? dt.name : dt.SystemType.Name;
                if (keys != null)
                {
                    int j;
                    for (j = 0; j < keys.Count; j++)
                        if (keys[j] == p)
                            break;
                    if (j < keys.Count)
                        sb.Append("  @Key(" + j + ")\r\n");
                }
                dt.FieldJava(cx, sb);
                sb.Append("  public " + tn + " " + cx.NameFor(p) + ";");
                sb.Append("\r\n");
            }
            sb.Append("}\r\n");
            return new TRow(cx,cx._Dom(from),new TChar(md.name),new TChar(key),
                new TChar(sb.ToString()));
        }
        /// <summary>
        /// Generate a row for the Role$Python table: includes a Python class definition
        /// </summary>
        /// <param name="from">The query</param>
        /// <param name="_enu">The object enumerator</param>
        /// <returns></returns>
        internal override TRow RolePythonValue(Context cx, DBObject from, ABookmark<long, object> _enu)
        {
            var md = cx._Dom(this);
            var mi = infos[cx.role.defpos];
            var versioned = true;
            var sb = new StringBuilder();
            sb.Append("# "); sb.Append(md.name); sb.Append(" Created on ");
            sb.Append(DateTime.Now);
            sb.Append("\r\n# from Database " + cx.db.name + ", Role " + cx.db.role.name + "\r\n");
            var key = BuildKey(cx, out CList<long> keys);
            if (mi.description != "")
                sb.Append("# " + mi.description + "\r\n");
            sb.Append("class " + md.name + (versioned ? "(Versioned)" : "") + ":\r\n");
            sb.Append(" def __init__(self):\r\n");
            if (versioned)
                sb.Append("  super().__init__('','')\r\n");
            for(var b = md.representation.First();b!=null;b=b.Next())
            {
                sb.Append("  self." + cx.NameFor(b.key()) + " = " + b.value().defaultValue);
                sb.Append("\r\n");
            }
            sb.Append("  self._schemakey = "); sb.Append(from.lastChange); sb.Append("\r\n");
            if (keys!=null)
            {
                var comma = "";
                sb.Append("  self._key = ["); 
                for (var i=0;i<keys.Count;i++)
                {
                    sb.Append(comma); comma = ",";
                    sb.Append("'");  sb.Append(keys[i]); sb.Append("'");
                }
                sb.Append("]\r\n");
            }
            return new TRow(cx,cx._Dom(from), new TChar(md.name),new TChar(key),
                new TChar(sb.ToString()));
        }
        internal virtual string BuildKey(Context cx,out CList<long> keys)
        {
            keys = null;
            for (var xk = indexes.First(); xk != null; xk = xk.Next())
                for (var c = xk.value().First();c!=null;c=c.Next())
            {
                var x = cx.db.objects[c.key()] as Index;
                if (x.tabledefpos != defpos)
                    continue;
                if ((x.flags & PIndex.ConstraintType.PrimaryKey) == PIndex.ConstraintType.PrimaryKey)
                    keys = x.keys;
            }
            var comma = "";
            var sk = new StringBuilder();
            if (keys != null)
                for (var i = 0; i < (int)keys.Count; i++)
                    if ( cx.db.objects[keys[i]] is TableColumn cd)
                    {
                        sk.Append(comma); comma = ",";
                        sk.Append(cd.infos[cx.db._role].name);
                    }
            return sk.ToString();
        }
    }
    internal class VirtualTable : Table
    {
        internal const long
            _RestView = -372; // long RestView
        public long restView => (long)(mem[_RestView] ?? -1L);
        internal VirtualTable(PTable pt, Role ro, Context cx) : base(pt, ro, cx)
        {
            cx.Add(this);
        }
        internal VirtualTable(Ident tn, Context cx, Domain dm)
            : this(new PTable(tn.ident, dm, tn.iix.dp, cx), cx)
        { }
        internal VirtualTable(PTable pt, Context cx)
            : this(pt, cx.db.role, cx)
        {  }
        protected VirtualTable(long dp, BTree<long, object> m) : base(dp, m) { }
        public static VirtualTable operator +(VirtualTable v, (long, object) x)
        {
            return (VirtualTable)v.New(v.mem + x);
        }
        internal override Basis New(BTree<long, object> m)
        {
            return new VirtualTable(defpos, m);
        }
        internal override DBObject Relocate(long dp)
        {
            return new VirtualTable(dp, mem);
        }
        internal override ObInfo _ObInfo(long ppos, string name, Grant.Privilege priv)
        {
            var ti = base._ObInfo(ppos, name, priv);
            return ti;
        }
        internal override Index FindPrimaryIndex(Context cx)
        {
            var rv = (RestView)cx.db.objects[restView];
            cx.obs += rv.framing.obs;
            for (var b = indexes.First(); b != null; b = b.Next())
                for (var c = b.value().First(); c != null; c = c.Next())
                {
                    var ix = (Index)cx.db.objects[c.key()];
                    if (ix.flags.HasFlag(PIndex.ConstraintType.PrimaryKey))
                        return ix;
                }
            return null;
        }
        internal override string BuildKey(Context cx, out CList<long> keys)
        {
            keys = null;
            var rv = (RestView)cx.db.objects[restView];
            cx.obs += rv.framing.obs;
            for (var xk = indexes.First(); xk != null; xk = xk.Next())
                for (var c = xk.value().First(); c != null; c = c.Next())
                {
                    var x = cx.db.objects[c.key()] as Index;
                    if (x.tabledefpos != defpos)
                        continue;
                    if ((x.flags & PIndex.ConstraintType.PrimaryKey) == PIndex.ConstraintType.PrimaryKey)
                        keys = x.keys;
                }
            var comma = "";
            var sk = new StringBuilder();
            if (keys != null)
            {
                for (var i = 0; i < (int)keys.Count; i++)
                {
                    var se = cx.obs[keys[i]];
                    sk.Append(comma);
                    comma = ",";
                    sk.Append(se.infos[cx.role.defpos].name);
                }
            }
            return sk.ToString();
        }
        public override string ToString()
        {
            var sb = new StringBuilder(base.ToString());
            sb.Append(" RestView="); sb.Append(Uid(restView));
            return sb.ToString();
        }
    }
}
