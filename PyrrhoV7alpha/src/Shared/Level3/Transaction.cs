﻿using Pyrrho.Common;
using Pyrrho.Level2;
using Pyrrho.Level4;
using System;
using System.Net;
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
    /// DBObjects with transaction uids are add to the transaction's list of objects.
    /// Transaction itself is not shareable because Physicals are mutable.
    /// 
    /// WARNING: Each new Physical for a transaction must be added to the Context
    /// so that Transaction gets a chance to update nextPos. Make sure you understand this fully
    /// before you add any code that creates a new Physical.
    /// shareable as at 26 April 2021
    /// </summary>
    internal class Transaction : Database
    {
        internal const long
            AutoCommit = -278, // bool
            Diagnostics = -280, // BTree<Sqlx,TypedValue>
            _ETags = -374, // CTree<string,string> url, ETag
            Physicals = -250, // BTree<long,Physical>
            Posts = -398, // bool
            StartTime = -217, // DateTime
            Step = -276, // long
            TriggeredAction = -288; // long
        public BTree<Sqlx, TypedValue> diagnostics =>
            (BTree<Sqlx,TypedValue>)(mem[Diagnostics]??BTree<Sqlx, TypedValue>.Empty);
        internal override long uid => (long)(mem[NextId]??-1L);
        public override long lexeroffset => uid;
        internal long step => (long)(mem[Step] ?? TransPos);
        internal override long nextPos => (long)(mem[NextPos]??TransPos);
        internal override string source => (string)(mem[SelectStatement.SourceSQL]??"");
        internal BTree<long, Physical> physicals =>
            (BTree<long,Physical>)(mem[Physicals]??BTree<long, Physical>.Empty);
        internal DateTime startTime => (DateTime?)mem[StartTime]??throw new PEException("PE48172");
        internal override bool autoCommit => (bool)(mem[AutoCommit]??true);
        internal bool posts => (bool)(mem[Posts] ?? false);
        internal long triggeredAction => (long)(mem[TriggeredAction]??-1L);
        internal CTree<string,string> etags => 
            (CTree<string,string>)(mem[_ETags]??CTree<string,string>.Empty);
        /// <summary>
        /// Physicals, SqlValues and Executables constructed by the transaction
        /// will use virtual positions above this mark (see PyrrhoServer.nextIid)
        /// </summary>
        public const long TransPos = 0x4000000000000000;
        public const long Analysing = 0x5000000000000000;
        public const long Executables = 0x6000000000000000;
        // actual start of Heap is given by conn.nextPrep for the connection (see Context(db))
        public const long HeapStart = 0x7000000000000000; //so heap starts after prepared statements
        /// <summary>
        /// As created from the Database: 
        /// via db.mem below we inherit its objects, and the session user and role
        /// </summary>
        /// <param name="db"></param>
        /// <param name="t"></param>
        /// <param name="sce"></param>
        /// <param name="auto"></param>
        internal Transaction(Database db,long t,string sce,bool auto) 
            :base(db.loadpos,db.mem+(NextId,t+1) + (StartTime, DateTime.Now) 
            +(AutoCommit,auto)+(SelectStatement.SourceSQL,sce))
        {  }
        protected Transaction(Transaction t,long p, BTree<long, object> m)
            : base(p, m)
        {  }
        internal override Basis New(BTree<long, object> m)
        {
            return new Transaction(this,loadpos, m);
        }
        public override Database New(long c, BTree<long, object> m)
        {
            return new Transaction(this, c, m);
        }
        public override Transaction Transact(long t,string sce,Connection con,bool? auto=null)
        {
            var r = this;
            if (auto == false && autoCommit)
                r += (AutoCommit, false);
            // Ensure the correct role amd user combination
            r += (Step, r.nextPos);
            if (t>=TransPos) // if sce is tranaction-local, we need to make space above nextIid
                r = r+ (NextId,t+1)+(SelectStatement.SourceSQL,sce);
            return r;
        }
        public override Database RdrClose(ref Context cx)
        {
            cx.values = CTree<long, TypedValue>.Empty;
            cx.cursors = BTree<long, Cursor>.Empty;
            cx.obs = ObTree.Empty;
            cx.result = -1L;
            // but keep rdC, etags
            if (!autoCommit)
                return this;
            else
            {
                var r = cx.db.Commit(cx);
                var aff = cx.affected;
                cx = new Context(r,cx.conn);
                cx.affected = aff;
                return r;
            }
        }
        internal override int AffCount(Context cx)
        {
            var c = 0;
            for (var b = ((Transaction)cx.db).physicals.PositionAt(step); b != null;
                b = b.Next())
                if (b.value() is Record || b.value() is Delete)
                    c++;
            return c;
        }
        public static Transaction operator +(Transaction d, (long, object) x)
        {
            var (dp, ob) = x;
            var m = d.mem;
            if (d.mem[dp] == ob)
                return d;
            return new Transaction(d,d.loadpos, m+x);
        }
        internal override DBObject? Add(Context cx,Physical ph, long lp)
        {
            if (cx.parse != ExecuteStatus.Obey && cx.parse!=ExecuteStatus.Compile)
                return null;
            cx.db += (Physicals,physicals +(ph.ppos, ph));
            cx.db += (NextPos, ph.ppos + 1);
            return ph.Install(cx, lp);
        }
        /// <summary>
        /// We commit unknown users to the database if necessary for audit.
        /// There is a theoretical danger here that a conncurrent transaction will
        /// have committed the same user id. Watch out for this and take appropriate action.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="cx"></param>
        public override void Audit(Audit a,Context cx)
        {
            if (databases[name] is not Database db || dbfiles[name] is not FileStream df
                || a.user is not User u)
                return;
            var wr = new Writer(new Context(db), df);
            // u is from this transaction which has not been committed.
            // it is possible that a different user u is in the database: check for name.
            if (u.defpos > TransPos && u.name!=null && db.roles.Contains(u.name)
                && db.objects[db.roles[u.name]] is User du)
                a.user = du;
            lock (wr.file)
            {
                wr.oldStmt = wr.cx.db.nextStmt;
                wr.segment = wr.file.Position;
                a.Commit(wr, this);
                wr.PutBuf();
                df.Flush();
            }
        }
        internal override Database Commit(Context cx)
        {
            if (cx == null)
                return Rollback();
            if (physicals == BTree<long, Physical>.Empty &&
                (autoCommit || (cx.rdC.Count == 0 && (cx.db as Transaction)?.etags == null)))
                return Rollback();
            // check for the case of an ad-hoc user that does not need to commit
            if (physicals.Count == 1L && physicals.First()?.value() is PUser)
                return Rollback();
            for (var b = cx.deferred.First(); b != null; b = b.Next())
            {
                var ta = b.value();
                ta.defer = false;
                ta.db = this;
                ta.Exec();
            }
            if (!autoCommit)
                for (var b = (cx.db as Transaction)?.etags.First(); b != null; b = b.Next())
                    if (b.key() != name)
                        cx.CheckRemote(b.key(), b.value());
            // Both rdr and wr access the database - not the transaction information
            if (databases[name] is not Database db || dbfiles[name] is not FileStream df)
                throw new PEException("PE0100");
            var rdr = new Reader(new Context(db), loadpos);
            var wr = new Writer(new Context(db), df);
            wr.cx.nextHeap = cx.nextHeap; // preserve Compiled objects framing
            var tb = physicals.First(); // start of the work we want to commit
            var since = rdr.GetAll();
            Physical? ph = null;
            PTransaction? pt = null;
            for (var pb = since.First(); pb != null; pb = pb.Next())
            {
                ph = pb.value();
                if (ph.type == Physical.Type.PTransaction)
                    pt = (PTransaction)ph;
                if (cx.rdS[ph._Table] is CTree<long, bool> ct)
                {
                    if (ct.Contains(-1L))
                    {
                        cx.rconflicts++;
                        throw new DBException("4008", ph._Table);
                    }
                    if (ct.Contains(ph.Affects) && pt!=null && ph.Conflicts(cx.rdC, pt) is Exception e)
                    {
                        cx.rconflicts++;
                        throw e;
                    }
                }
                /*
                for (var cb = cx.rdC.First(); cb != null; cb = cb.Next())
                {
                    var ce = cb.value()?.Check(ph,pt);
                    if (ce != null)
                    {
                        cx.rconflicts++;
                        throw ce;
                    }
                }
                */
                if (pt!=null)
                for (var b = tb; b != null; b = b.Next())
                {
                    var p = b.value();
                    var ce = ph.Conflicts(rdr.context.db, cx, p, pt);
                    if (ce!=null)
                    {
                        cx.wconflicts++;
                        throw ce;
                    }
                }
            }
            lock (wr.file)
            { 
                if (databases[name] is Database nd && nd!=db)// may have moved on 
                    db = nd; 
                rdr = new Reader(new Context(db), ph?.ppos ?? loadpos); 
                rdr.locked = true;
                since = rdr.GetAll(); // resume where we had to stop above, use new file length
                for (var pb = since.First(); pb != null; pb = pb.Next())
                {
                    ph = pb.value();
                    PTransaction? pu = null;
                    if (ph.type == Physical.Type.PTransaction)
                        pu = (PTransaction)ph;
                    if (cx.rdS[ph._Table] is CTree<long, bool> ct)
                    {
                        if (ct.Contains(-1L))
                        {
                            cx.rconflicts++;
                            throw new DBException("4008", ph._Table);
                        }
                        if (ct.Contains(ph.Affects) && pu!=null && ph.Conflicts(cx.rdC, pu) is Exception e)
                        {
                            cx.rconflicts++;
                            throw e;
                        }
                    }
                    /*
                    for (var cb = cx.rdC.First(); cb != null; cb = cb.Next())
                    {
                        var ce = cb.value()?.Check(ph,pu);
                        if (ce != null)
                        {
                            cx.rconflicts++;
                            throw ce;
                        }
                    }
                    */
                    if (pu!=null)
                    for (var b = tb; b != null; b = b.Next())
                    {
                        var ce = ph.Conflicts(rdr.context.db, cx, b.value(), pu);
                        if (ce != null)
                        {
                            cx.wconflicts++;
                            throw ce;
                        }
                    }
                }
                if (physicals.Count == 0)
                    return Rollback();
                pt = new PTransaction((int)physicals.Count, user, role, nextPos);
                cx.Add(pt);
                wr.segment = wr.file.Position;
                var (tr, _) = pt.Commit(wr, this);
                for (var b = physicals.First(); b != null; b = b.Next())
                {
                    (tr, _) = b.value().Commit(wr, tr);
                    if (PyrrhoStart.TutorialMode)
                        Console.WriteLine("Committed " + b.value());
                }
                cx.affected = (cx.affected ?? Rvv.Empty) + wr.cx.affected;
                wr.PutBuf();
                df.Flush();
                wr.cx.db += (LastModified, File.GetLastWriteTimeUtc(name));
                wr.cx.result = -1L;
                var r = new Database(wr.Length,wr.cx.db.mem);
                lock (_lock)
                    databases += (name, r-Role-User);
                cx.db = r;
                return r;
            }
        }
        /// <summary>
        /// Contsruct a DBException and add in some diagnostics information
        /// </summary>
        /// <param name="sig">The name of the exception</param>
        /// <param name="obs">The objects for the format string</param>
        /// <returns>the DBException</returns>
        public override DBException Exception(string sig, params object[] obs)
        {
            var r = new DBException(sig, obs);
            for (var s = diagnostics.First(); s != null; s = s.Next())
                r.Add(s.key(), s.value());
            r.Add(Sqlx.CONNECTION_NAME, new TChar(name));
#if !EMBEDDED
            r.Add(Sqlx.SERVER_NAME, new TChar(PyrrhoStart.host));
#endif
            if (diagnostics[Sqlx.TRANSACTIONS_COMMITTED] is TypedValue tc)
                r.Add(Sqlx.TRANSACTIONS_COMMITTED, tc);
            if (diagnostics[Sqlx.TRANSACTIONS_ROLLED_BACK] is TypedValue rb)
                r.Add(Sqlx.TRANSACTIONS_ROLLED_BACK, rb);
            return r;
        }
        internal Context Execute(Executable e, Context cx)
        {
            if (cx.parse != ExecuteStatus.Obey)
                return cx;
            var a = new Activation(cx,e.label??"");
            a.exec = e;
            var ac = e.Obey(a);
            if (a.signal != null)
            {
                var ex = Exception(a.signal.signal, a.signal.objects);
                for (var s = a.signal.setlist.First(); s != null; s = s.Next())
                    if (cx.obs[s.value()] is SqlValue v)
                        ex.Add(s.key(), v.Eval(cx));
                throw ex;
            }
            cx.result = -1L;
            if (cx != ac)
            {
                cx.db = ac.db;
                cx.rdC = ac.rdC;
            }
            return cx;
        }
        /// <summary>
        /// For REST service: do what we should according to the path, mime type and posted obs
        /// </summary>
        /// <param name="method">GET/HEAD/PUT/POST/DELETE</param>
        /// <param name="path">The URL</param>
        /// <param name="mime">The mime type in the header</param>
        /// <param name="sdata">The posted obs if any</param>
        internal Context Execute(Context cx, long sk, string method, string dn, string[] path, 
            string query, string? mime, string sdata)
        {
            var db = this;
            cx.inHttpService = true;
            int j, ln;
            if (sk!=0L)
            {
                j = 1; ln = 2;
            } else
            {
                j = 2; ln = 4;
            }
            if (path.Length >= ln)
            {
                RowSet? fm = null;
                if (cx.role != null && long.TryParse(path[j], out long t) && cx.db.objects[t] is Table tb
                    && tb.infos[cx.role.defpos] is ObInfo ti && ti.name != null &&
                    cx._Dom(tb) is Domain dm)
                {
                    if (sk != 0L && ti.schemaKey > sk)
                        throw new DBException("2E307", ti.name);
                    fm = tb.RowSets(new Ident(ti.name, cx.GetIid()), cx, dm, cx.GetPrevUid());
                    j++;
                }
                switch (method)
                {
                    case "HEAD":
                        cx.result = -1L;
                        break;
                    case "GET":
                        db.Execute(cx, fm, method, dn, path, query, j);
                        break;
                    case "DELETE":
                        {
                            db.Execute(cx, fm, method, dn, path, query, j);
                            if (cx.obs[cx.result] is TableRowSet trd)
                                cx = db.Delete(cx, trd);
                            break;
                        }
                    case "PUT":
                        {
                            db.Execute(cx, fm, method, dn, path, query, j);
                            if (cx.obs[cx.result] is TableRowSet trp)
                                cx = db.Put(cx, trp, sdata);
                            break;
                        }
                    case "POST":
                        {
                            db.Execute(cx, fm, method, dn, path, query, j);
                            if (cx.obs[cx.result] is TableRowSet trt)
                                cx = db.Post(cx, trt, sdata);
                            break;
                        }
                }
            }
            else
            {
                switch (method)
                {
                    case "POST":
                        new Parser(cx).ParseSql(sdata);
                        break;
                }
            }
            return cx;
        }
        /// <summary>
        /// HTTP service implementation
        /// See sec 3.8.2 of the Pyrrho manual. The URL format is very flexible, with
        /// keywords such as table, procedure all optional. 
        /// URL encoding is used so that at this stage the URL can contain spaces etc.
        /// The URL is case-sensitive throughout, so use capitals a lot,
        /// except for the keywords specified in 3.8.2.
        /// Single quotes around string values and double quotes around identifiers
        /// are optional (they can be used to disambiguate column names from string values,
        /// or to include commas etc in string values).
        /// Expressions are allowed in procedure argument values,
        /// Where conditions can only be simple column compareop value (can be chained),
        /// and no other expressions are allowed.
        /// The query part of the URL is used for metadata flags, see section 7.2.
        /// </summary>
        /// <param name="cx">The context</param>
        /// <param name="f">The rowset so far</param>
        /// <param name="method">GET, PUT, POST or DELETE</param>
        /// <param name="dn">The database name</param>
        /// <param name="path">The URL split into segments by /</param>
        /// <param name="query">The metadata flags part of the query</param>
        /// <param name="p">Where we are in the path</param>
        internal void Execute(Context cx, RowSet? f,string method, string dn, string[] path, string query, int p)
        {
            if ((p >= path.Length || path[p] == "") && f!=null)
            {
                cx.result = f.defpos;
                return;
            }
            string cp = path[p]; // Test cp against Selector and Processing specification in 3.8.2
            int off = 0;
            string[] sp = cp.Split(' ');
            CallStatement? fc = null;
            switch (sp[0])
            {
                case "edmx":
                    break;
                case "table":
                    {
                        var tbs = cp[(6 + off)..];
                        tbs = WebUtility.UrlDecode(tbs);
                        var tbn = new Ident(tbs, cx.GetIid());
                        if(cx.db==null || cx.db.role==null ||objects[cx.db.role.dbobjects[tbn.ident]] is not Table tb
                            || cx._Dom(tb) is not Domain dm)
                            throw new DBException("42107", tbn).Mix();
                        if (f==null)
                            f = tb.RowSets(tbn,cx,dm,tbn.iix.dp);
                        var lp = cx.Ix(uid + 6 + off);
                        break;
                    }
                case "procedure":
                    {
                        if (fc == null)
                        {
                            var pn = cp[(10 + off)..];
#if (!SILVERLIGHT) && (!ANDROID)
                            pn = WebUtility.UrlDecode(pn);
#endif
                            fc = new Parser(cx).ParseProcedureCall(pn,Domain.Content);
                        }
                        if (cx.db.objects[fc.procdefpos] is not Procedure pr)
                            throw new DBException("42108", cp);
                        pr.Exec(cx, fc.parms);
                        break;
                    }
                case "key":
                    {
                        if (f is TableRowSet ts && objects[f.target] is Table tb &&
                            tb.FindPrimaryIndex(cx) is Index ix)
                        {
                            var kn = 0;
                            var fl = CTree<long, TypedValue>.Empty;
                            while (kn < ix.keys.Length && p < path.Length)
                            {
                                var sk = path[p];
                                if (kn == 0)
                                    sk = sk[(4 + off)..];
#if (!SILVERLIGHT) && (!ANDROID)
                                sk = WebUtility.UrlDecode(sk);
#endif
                                if (cx.obs[ix.keys[kn]] is not TableColumn tc || cx._Dom(tc) is not Domain ft)
                                    throw new DBException("42112", kn);
                                if (ft.TryParse(new Scanner(uid, sk.ToCharArray(), 0, cx), out TypedValue? kv) != null)
                                    break;
                                kn++;
                                p++;
                                fl += (ts.iSMap[tc.defpos], kv);
                            }
                            var rs = (RowSet)cx.Add(f + (RowSet._Matches,fl));
                            cx.result = rs.defpos;
                            break;
                        }
                        goto case "where";
                    }
                case "where":
                    {
                        string ks = cp[(6 + off)..];
#if (!SILVERLIGHT) && (!ANDROID)
                        ks = WebUtility.UrlDecode(ks);
#endif
                        if (f == null)
                            throw new DBException("42000", ks).ISO();
                        string[] sk = Array.Empty<string>();
                        if (ks.Contains("={") || ks[0] == '{')
                            sk = new string[] { ks };
                        else
                            sk = ks.Split(',');
                        var n = sk.Length;
                        var psr = new Parser(cx);
                        var wt = CTree<long, bool>.Empty;
                        for (var si = 0; si<n; si++)
                          wt += psr.ParseSqlValue(new Ident(sk[si],cx.GetIid()), Domain.Bool).Disjoin(cx);
                        cx.Add(f + (RowSet._Where,wt));
                        break;
                    }
                case "distinct":
                    {
                        if (cp.Length < 10 && cx.obs[cx.result] is RowSet r)
                        {
                            cx.val = (TypedValue?)new DistinctRowSet(cx,r).First(cx)??TNull.Value;
                            break;
                        }
                        string[] ss = cp[9..].Split(',');
                        // ???
                        break;
                    }
                case "ascending":
                    {
                        if (cp.Length < 10)
                            throw new DBException("42161", "Column(s)", cp).Mix();
                        string[] ss = cp[9..].Split(',');
                        //??
                        break;
                    }
                case "descending":
                    {
                        if (cp.Length < 10)
                            throw new DBException("42161", "Column(s)", cp).Mix();
                        string[] ss = cp[9..].Split(',');
                        // ??
                        break;
                    }
                case "skip":
                    {
                        //                transaction.SetResults(new RowSetSection(transaction.result.rowSet, int.Parse(cp.Substring(5)), int.MaxValue));
                        break;
                    }
                case "count":
                    {
                        //                transaction.SetResults(new RowSetSection(transaction.result.rowSet, 0, int.Parse(cp.Substring(6))));
                        break;
                    }
                case "of":
                    {
                        var s = cp[(3 + off)..];
                        var ps = s.IndexOf('(');
                        var key = new string[0];
                        if (ps > 0)
                        {
                            var cs = s.Substring(ps + 1, s.Length - ps - 2);
                            s = s.Substring(0, ps - 1);
                            key = cs.Split(',');
                        }
                        // ??
                        break;
                    }
                case "rvv":
                    {
                        var s = cp[(4 + off)..];
                        // ??
                        return; // do not break;
                    }
                case "select":
                    {
                        sp[0] = sp[0].Trim(' ');
                        if (f is TableRowSet fa && objects[fa.target] is Table ta && cx._Dom(ta) is Domain dm
                            && cx.role!=null)
                        {
                            var cs = sp[0].Split(',');
                            var ns = CTree<string, long>.Empty;
                            var ss = CList<long>.Empty;
                            for (var c = dm.rowType.First(); c != null; c = c.Next())
                            if (cx._Ob(c.value()) is DBObject oa && oa.infos[cx.role.defpos] is ObInfo ci && ci.name!=null)
                                ns += (ci.name, c.value());
                            for (var i = 0; i < cs.Length && ns.Contains(cs[i]); i++)
                                ss += fa.iSMap[ns[cs[i]]];
                            if (ss != CList<long>.Empty && cx._Dom(f) is Domain df)
                            {
                                var fd = new Domain(cx.GetUid(), cx, df.kind, df.representation, ss);
                                f = new SelectedRowSet(cx, fd.defpos, f);
                                break;
                            }
                        }
                        throw new DBException("420000", cp);
                    }
                default:
                    {
                        var cn = sp[0];
                        cn = WebUtility.UrlDecode(cn);
                        if (QuotedIdent(cn))
                            cn = cn.Trim('"');
                        var ob = GetObject(cn,cx.role);
                        if (ob is Table tb)
                        {
                            off = -6;
                            goto case "table";
                        }
                        else if (ob is Role ro)
                        {
                            cx.db += (Role, ro);
                            Execute(cx, f, method, dn, path, query, p + 1);
                            return;
                        }
                        else if (ob is Procedure pn)
                        {
                            off = -10;
                            goto case "procedure";
                        }
                        if (cn.Contains(":"))
                        {
                            off -= 4;
                            goto case "rvv";
                        }
                        if (cn.Contains('=') ||cn.Contains('<') ||cn.Contains('>'))
                        {
                            off = -6;
                            goto case "where";
                        }
                        if (f is TableRowSet fa && objects[fa.target] is Table ta && cx._Dom(ta) is Domain dm && cx.role!=null)
                        {
                            var cs = sp[0].Split(',');
                            var ns = CTree<string, long>.Empty;
                            var ss = CList<long>.Empty;
                            for (var c = dm.rowType.First();c!=null;c=c.Next())
                            if (cx._Ob(c.value()) is DBObject co && co.infos[cx.role.defpos] is ObInfo ci &&
                                    ci.name!=null)
                                ns += (ci.name, c.value());
                            for (var i = 0; i<cs.Length &&  ns.Contains(cs[i]);i++)
                            {
                                ss += fa.iSMap[ns[cs[i]]];
                            }
                            if (ss!=CList<long>.Empty && cx._Dom(f) is Domain df)
                            {
                                var fd = new Domain(cx.GetUid(),cx,df.kind,df.representation,ss);
                                f = new SelectedRowSet(cx, fd.defpos, f);
                                break;
                            }
                            var ix = ta.FindPrimaryIndex(cx);
                            if (ix != null)
                            {
                                off -= 4;
                                goto case "key";
                            }
                        }
                        if (cx.val != TNull.Value)
                        {
                            off = -4;
                            goto case "key";
                        }
                        break;
                    //    throw new DBException("42107", sp[0]).Mix();
                    }
            }
            Execute(cx, f, method, dn, path, query, p + 1);
        }
        bool QuotedIdent(string s)
        {
            var cs = s.ToCharArray();
            var n = cs.Length;
            if (n <= 3 || cs[0] != '"' || cs[n-1]!='"')
                return false;
            for (var i = 1; i < n - 1; i++)
                if (!char.IsLetterOrDigit(cs[i]) && cs[i] != '_')
                    return false;
            return true;
        }
        internal override Context Put(Context cx, TableRowSet rs, string s)
        {
            var da = new TDocArray(s);
            if (objects[rs.target] is not Table tb || cx.obs.Last() is not ABookmark<long,DBObject> ab
                || cx._Dom(rs) is not Domain dr || cx.role==null)
                throw new PEException("PE49000");
            var ix = tb.FindPrimaryIndex(cx);
            var us = rs.assig;
            var ma = CTree<long,TypedValue>.Empty;
            var d = da[0];
            cx.nextHeap = ab.key()+1;
            for (var c = dr.rowType.First(); c != null; c = c.Next())
            {
                var n = "";
                var isk = false;
                if (cx.obs[c.value()] is SqlValue sv && sv.name!=null)
                {
                    n = sv.name;
                    if (sv is SqlCopy sc && ix!=null)
                        isk = ix.keys.rowType.Has(sc.copyFrom);
                }
                else if (cx._Ob(c.value()) is DBObject oc && oc.infos[cx.role.defpos] is ObInfo ci && ci.name!=null)
                    n = ci.name;
                if (d[n] is not TypedValue v)
                    throw new PEException("PE49203"); 
                if (isk)
                    ma += (c.value(), v);  
                else
                {
                    var sl = new SqlLiteral(cx.GetUid(), cx, v);
                    cx._Add(sl);
                    us += (new UpdateAssignment(c.value(), sl.defpos), true);
                }
            }
            rs += (RowSet._Matches, ma);
            rs += (RowSet.Assig, us);
            cx.Add(rs);
            rs = (TableRowSet)(cx.obs[rs.defpos]??throw new PEException("PE49202"));
            var ta = rs.Update(cx, rs)[rs.target];
            if (ta != null)
            {
                ta.db = cx.db;
                if (rs.First(cx) is Cursor cu)
                {
                    ta.cursors = cx.cursors + (rs.defpos, cu);
                    ta.EachRow(0);
                }
                cx.db = ta.db;
            }
            return cx;
        }
        internal override Context Post(Context cx, TableRowSet rs, string s)
        {
            var da = new TDocArray(s);
            var rws = BList<(long, TRow)>.Empty;
            if (cx._Dom(rs) is Domain dm)
            {
                for (var i = 0; i < da.Count; i++)
                {
                    var d = da[i];
                    var vs = CTree<long, TypedValue>.Empty;
                    for (var c = dm.rowType.First(); c != null; c = c.Next())
                        if (cx.NameFor(c.value()) is string n && d[n] is TypedValue v)
                            vs += (c.value(), v);
                    rws += (cx.GetUid(), new TRow(dm, vs));
                }
                var ers = new ExplicitRowSet(cx.GetUid(), cx, dm, rws);
                cx.Add(ers);
                if (ers.First(cx) is Cursor cu)
                {
                    if (ers.tree != null)
                        rs = rs + (Index.Tree, ers.tree) + (Index.Keys, ers.keys);
                    if (rs.Insert(cx, rs, dm)[rs.target] is TargetActivation ta)
                    {
                        ta.db = cx.db;
                        cx.cursors += (rs.defpos, cu);
                        ta.cursors = cx.cursors;
                        ta.EachRow(0);
                    }
                }
            }
            return cx;
        }
        internal override Context Delete(Context cx, TableRowSet r)
        {
            if (cx.obs[r.from] is RowSet fm)
            {
                var ts = r.Delete(cx, fm);
                if (ts.First()?.value() is TargetActivation ta)
                    for (var b = r.First(cx); b != null; b = b.Next(cx))
                        if (cx.cursors[r.target] is Cursor ib)
                        {
                            ta.db = cx.db;
                            cx.cursors += (fm.defpos, ib);
                            ta.EachRow(b._pos);
                            cx.db = ta.db;
                        }
            }
            return base.Delete(cx, r);
        }
        /// <summary>
        /// Implement Grant or Revoke
        /// </summary>
        /// <param name="grant">true=grant,false=revoke</param>
        /// <param name="pr">the privilege</param>
        /// <param name="obj">the database object</param>
        /// <param name="grantees">a list of grantees</param>
        void DoAccess(Context cx, bool grant, Grant.Privilege pr, long obj,
            DBObject[] grantees)
        {
            var np = cx.db.nextPos;
            if (grantees != null) // PUBLIC
                foreach (var mk in grantees)
                {
                    long gee = -1;
                    gee = mk.defpos;
                    if (grant)
                        cx.Add(new Grant(pr, obj, gee, np++, cx));
                    else
                        cx.Add(new Revoke(pr, obj, gee, np++, cx));
                }
        }
        /// <summary>
        /// Implement Grant/Revoke on a list of TableColumns
        /// </summary>
        /// <param name="grant">true=grant, false=revoke</param>
        /// <param name="tb">the database</param>
        /// <param name="pr">the privileges</param>
        /// <param name="tb">the table</param>
        /// <param name="list">(Privilege,columnnames[])</param>
        /// <param name="grantees">a list of grantees</param>
        void AccessColumns(Context cx, bool grant, Grant.Privilege pr, Table tb, PrivNames list, DBObject[] grantees)
        {
            var rt = cx._Dom(tb) ?? Domain.Content;
            var ne = list.cols != BTree<string, bool>.Empty;
            for (var b = rt.representation.First(); b != null; b = b.Next())
                if (cx._Ob(b.key()) is DBObject oc && oc.infos[cx.role.defpos] is ObInfo ci && ci.name != null
                    && !ne && list.cols.Contains(ci.name))
                {
                    list.cols -= ci.name;
                    DoAccess(cx, grant, pr, b.key(), grantees);
                }
            if (list.cols.First()?.key() is string cn)
                throw new DBException("42112", cn);
        }
        /// <summary>
        /// Implement grant/revoke on a Role
        /// </summary>
        /// <param name="grant">true=grant, false=revoke</param>
        /// <param name="roles">a list of Roles (ids)</param>
        /// <param name="grantees">a list of Grantees</param>
        /// <param name="opt">whether with ADMIN option</param>
		internal void AccessRole(Context cx, bool grant, string[] rols, DBObject[] grantees, bool opt)
        {
            var db = this;
            Grant.Privilege op = Grant.Privilege.NoPrivilege;
            if (opt == grant) // grant with grant option or revoke
                op = Grant.Privilege.UseRole | Grant.Privilege.AdminRole;
            else if (opt && !grant) // revoke grant option for
                op = Grant.Privilege.AdminRole;
            else // grant
                op = Grant.Privilege.UseRole;
            foreach (var s in rols)
                if (roles.Contains(s) && objects[roles[s]] is Role ro)
                    DoAccess(cx, grant, op, ro.defpos, grantees);
        }
        /// <summary>
        /// Implement grant/revoke on a database obejct
        /// </summary>
        /// <param name="grant">true=grant, false=revoke</param>
        /// <param name="privs">the privileges</param>
        /// <param name="dp">the database object defining position</param>
        /// <param name="grantees">a list of grantees</param>
        /// <param name="opt">whether with GRANT option (grant) or GRANT for (revoke)</param>
        internal void AccessObject(Context cx, bool grant, PrivNames[] privs, long dp, DBObject[] grantees, bool opt)
        {
            if (role!=null && objects[dp] is DBObject ob && ob.infos[role.defpos] is ObInfo gd)
            {
                Grant.Privilege defp = Grant.Privilege.NoPrivilege;
                if (!grant)
                    defp = (Grant.Privilege)0x3fffff;
                var p = defp; // the privilege being granted
                var gp = gd.priv; // grantor's privileges on the target object
                var changed = true;
                if (privs == null) // all (grantor's) privileges
                {
                    if (grant)
                    {
                        p = gp;
                        for (var cp = cx._Dom(ob)?.rowType.First(); cp != null; cp = cp.Next())
                        {
                            var c = cp.value();
                            gp = gd.priv;
                            var pp = defp;
                            if (grant)
                                pp = gp;
                            DoAccess(cx, grant, pp, c, grantees);
                        }
                    }
                }
                else
                    foreach (var mk in privs)
                    {
                        Grant.Privilege q = Grant.Privilege.NoPrivilege;
                        switch (mk.priv)
                        {
                            case Sqlx.SELECT: q = Grant.Privilege.Select; break;
                            case Sqlx.INSERT: q = Grant.Privilege.Insert; break;
                            case Sqlx.DELETE: q = Grant.Privilege.Delete; break;
                            case Sqlx.UPDATE: q = Grant.Privilege.Update; break;
                            case Sqlx.REFERENCES: q = Grant.Privilege.References; break;
                            case Sqlx.EXECUTE: q = Grant.Privilege.Execute; break;
                            case Sqlx.TRIGGER: break; // ignore for now (?)
                            case Sqlx.USAGE: q = Grant.Privilege.Usage; break;
                            case Sqlx.OWNER:
                                q = Grant.Privilege.Owner;
                                if (!grant)
                                    throw Exception("4211A", mk).Mix();
                                break;
                            default: throw Exception("4211A", mk).Mix();
                        }
                        Grant.Privilege pp = (Grant.Privilege)(((int)q) << 12);
                        if (opt == grant)
                            q |= pp;
                        else if (opt && !grant)
                            q = pp;
                        if (mk.cols.Count != 0L)
                        {
                            if (changed)
                                changed = grant;
                            AccessColumns(cx, grant, q, (Table)ob, mk, grantees);
                        }
                        else
                            p |= q;
                    }
                if (changed)
                    DoAccess(cx, grant, p, ob?.defpos ?? 0, grantees);
            }
        }
        /// <summary>
        /// Called from the Parser.
        /// Create a new level 2 index associated with a referential constraint definition.
        /// </summary>
        /// <param name="tb">A table</param>
        /// <param name="name">The name for the index</param>
        /// <param name="key">The set of TableColumns defining the foreign key</param>
        /// <param name="rt">The referenced table</param>
        /// <param name="refs">The set of TableColumns defining the referenced key</param>
        /// <param name="ct">The constraint type</param>
        /// <param name="afn">The adapter function if specified</param>
        /// <param name="cl">The set of Physicals being gathered by the parser</param>
        public PIndex ReferentialConstraint(Context cx,Table tb, string name,
            Domain key,Table rt, Domain refs, PIndex.ConstraintType ct, 
            string afn)
        {
            Index? rx = null;
            if (refs.Length == 0)
                rx = rt.FindPrimaryIndex(cx);
            else
                rx = rt.FindIndex(this, refs)?[0];
            if (rx == null)
                throw new DBException("42111").Mix();
            if (rx.keys.Length != key.Length)
                throw new DBException("22207").Mix();
            return new PIndex1(name, tb, key, ct, rx.defpos, afn,
                nextPos);
        }
  /*      public VIndex ReferentialConstraint(Context cx, Table tb, Ident name,
    CList<int> key, Table rt, CList<long> refs, PIndex.ConstraintType ct, int c)
        {
            Index rx = null;
            if (refs == null || refs.Count == 0)
                rx = rt.FindPrimaryIndex(cx);
            else
                rx = rt.FindIndex(this, refs)?[0];
            if (rx == null)
                throw new DBException("42111").Mix();
            if (rx.keys.Count != key.Count)
                throw new DBException("22207").Mix();
            return new VIndex(name.ident, tb.defpos, key, ct, rx.defpos, nextPos+c+1, cx);
        } */
    }
    /// <summary>
    ///  better implementation of UNDO handler: copy the context stack as well as LocationTransaction states
    /// </summary>
    internal class ExecState
    {
        public Transaction mark;
        public Context stack;

        internal ExecState(Context cx,Transaction tr)
        {
            mark = tr;
            stack = cx;
        }
    }
}
