using Pyrrho.Common;
using Pyrrho.Level2;
using Pyrrho.Level3;
using Pyrrho.Level5;
using System.ComponentModel.Design;
using System.Globalization;
using System.Runtime.InteropServices;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2023
//
// This software is without support and no liability for damage consequential to use.
// You can view and test this code, and use it subject for any purpose.
// You may incorporate any part of this code in other software if its origin 
// and authorship is suitably acknowledged.
// All other use or distribution or the construction of any product incorporating 
// this technology requires a license from the University of the West of Scotland.

namespace Pyrrho.Level4
{
    /// <summary>
    /// An LL(1) Parser deals with all Sql statements from various sources.
    /// 
    /// Entries to the Parser from other files are marked internal or public
    /// and can be distinguished by the fact that they create a new Lexer()
    /// 
    /// Most of the grammar below comes from the SQL standard
    /// the comments are extracts from Pyrrho.doc synopsis of syntax
    /// 
    /// Many SQL statements parse to Executables (can be placed in stored procedures etc)
    /// Can then be executed imediately depending on parser settings.
    /// 
    /// Some constructs get parsed during database Load(): these should never try to change the schema
    /// or make other changes. parse should only call Obey within a transaction.
    /// This means that (for now at least) stored executable code (triggers, procedures) should
    /// never attempt schema changes. 
    /// </summary>
	internal class Parser
    {
        /// <summary>
        /// cx.obs contains DBObjects currently involved in the query (other than Domains).
        /// Domains are mostly unknown during query analysis: their "defining position"
        /// will be the lexical position of a DBObject being constructed in the parse.
        /// Any identifiers in the parse will be collected in cx.defs: if unknown their
        /// domain will usually be Domain.Content.
        /// </summary>
        public Context cx; // updatable: the current state of the parse
        /// <summary>
        /// The lexer breaks the input into tokens for us. During parsing
        /// lxr.val is the object corresponding to the current token,
        /// lxr.start and lxr.pos delimit the current token
        /// </summary>
		internal Lexer lxr;
        /// <summary>
        /// The current token
        /// </summary>
		internal Sqlx tok;
        public Parser(Database da,Connection con)
        {
            cx = new Context(da, con)
            {
                db = da.Transact(da.nextId, da.source, con)
            };
            lxr = new Lexer(cx, "");
        }
        public Parser(Context c)
        {
            cx = c;
            lxr = new Lexer(cx,"");
        }
        /// <summary>
        /// Create a Parser for Constraint definition
        /// </summary>
        /// <param name="_cx"></param>
        /// <param name="src"></param>
        /// <param name="infos"></param>
        public Parser(Context _cx,Ident src)
        {
            cx = _cx.ForConstraintParse();
            cx.parse = ExecuteStatus.Parse;
            lxr = new Lexer(cx,src);
            tok = lxr.tok;
        }
        public Parser(Context _cx, string src)
        {
            cx = _cx.ForConstraintParse();
            lxr = new Lexer(cx,new Ident(src, cx.Ix(Transaction.Analysing,cx.db.nextStmt)));
            tok = lxr.tok;
        }
        /// <summary>
        /// Create a Parser for Constraint definition
        /// </summary>
        /// <param name="rdr"></param>
        /// <param name="scr"></param>
        /// <param name="tb"></param>
        public Parser(Reader rdr, Ident scr) 
            : this(rdr.context, scr) 
        {  }
        internal Iix LexPos()
        {
            var lp = lxr.Position;
            switch (cx.parse)
            {
                case ExecuteStatus.Obey:
                    return cx.Ix(lp, lp);
                case ExecuteStatus.Prepare:
                    return new Iix(lp,cx,cx.nextHeap++);
                default:
                    return new Iix(lp, cx, cx.GetUid());
            }
        }
        internal long LexDp()
        {
            switch (cx.parse)
            {
                case ExecuteStatus.Obey:
                    return lxr.Position;
                case ExecuteStatus.Prepare:
                    return cx.nextHeap++;
                default:
                    return cx.GetUid();
            }
        }
        /// <summary>
        /// Move to the next token
        /// </summary>
        /// <returns></returns>
		internal Sqlx Next()
        {
            tok = lxr.Next();
            return tok;
        }
        /// <summary>
        /// Match any of a set of token types
        /// </summary>
        /// <param name="s">the list of token types</param>
        /// <returns>whether the current token matched any of the set</returns>
		bool Match(params Sqlx[] s)
        {
            string a = "";
            if (tok == Sqlx.ID)
                a = lxr.val.ToString().ToUpper();
            for (int j = 0; j < s.Length; j++)
            {
                if (tok == s[j])
                    return true;
                if (tok == Sqlx.ID && a.CompareTo(s[j].ToString())==0)
                {
                    lxr.tok = tok = s[j];
                    return true;
                }
            }
            return false;
        }
        /// <summary>
        /// Raise a syntax error if the current token does not match a given set
        /// </summary>
        /// <param name="t">the list of token types</param>
        /// <returns>the token that matched</returns>
		internal Sqlx Mustbe(params Sqlx[] t)
        {
            int j;
            string s = "";
            if (tok == Sqlx.ID)
                s = lxr.val.ToString().ToUpper();
            for (j = 0; j < t.Length; j++)
            {
                if (tok == t[j])
                    break;
                var a = tok == Sqlx.ID;
                var b = s == t[j].ToString();
                if (a && b)
                {
                    tok = t[j];
                    break;
                }
            }
            if (j >= t.Length)
            {
                string str = "";
                for (int k = 0; k < t.Length; k++)
                {
                    if (k > 0)
                        str += ", ";
                    str += t[k].ToString();
                }
                string ctx = (lxr.pos>=lxr.input.Length)?"EOF":new string(lxr.input, lxr.start, lxr.pos - lxr.start);
                throw new DBException("42161", str, ctx).Mix();
            }
            Next();
            return t[j];
        }
        /// <summary>
        /// Parse Sql input
        ///     Sql = SqlStatement [�;�] .
        /// The type of database modification that may occur is determined by db.parse.
        /// </summary>
        /// <param name="sql">the input</param>
        /// <param name="xp">the expected result type (default is Domain.Content)</param>
        /// <returns>The modified Database and the new uid highwatermark </returns>
        public Database ParseSql(string sql, Domain xp)
        {
            if (PyrrhoStart.ShowPlan)
                Console.WriteLine(sql);
            lxr = new Lexer(cx, sql, cx.db.lexeroffset);
            tok = lxr.tok;
            ParseSqlStatement(xp);
            if (tok == Sqlx.SEMICOLON)
                Next();
            for (var b = cx.forReview.First(); b != null; b = b.Next())
                if (cx.obs[b.key()] is SqlValue k)
                    for (var c = b.value().First(); c != null; c = c.Next())
                        if (cx.obs[c.key()] is RowSet rs)
                            rs.Apply(new BTree<long, object>(RowSet._Where, new CTree<long, bool>(k.defpos, true)),
                                cx);
            if (tok != Sqlx.EOF)
            {
                string ctx = new (lxr.input, lxr.start, lxr.pos - lxr.start);
                throw new DBException("42000", ctx).ISO();
            }
            if (cx.undefined != CTree<long, bool>.Empty)
                throw new DBException("42112", cx.obs[cx.undefined.First()?.key()??-1L]?.mem[ObInfo.Name] ?? "?");
            if (LexDp() > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
            return cx.db;
        }
        public Database ParseSql(string sql)
        {
            if (PyrrhoStart.ShowPlan)
                Console.WriteLine(sql);
            lxr = new Lexer(cx,sql, cx.db.lexeroffset);
            tok = lxr.tok;
            do
            {
                ParseSqlStatement(Domain.TableType);
                //      cx.result = e.defpos;
                if (tok == Sqlx.SEMICOLON)
                    Next();
            } while (tok != Sqlx.EOF);
            if (LexDp() > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
            return cx.db;
        }
        /// <summary>
        /// Add the parameters to a prepared statement
        /// </summary>
        /// <param name="pre">The object with placeholders</param>
        /// <param name="s">The parameter strings concatenated by |</param>
        /// <returns>The modified database and the new uid highwatermark</returns>
        public Database ParseSql(PreparedStatement pre,string s)
        {
            cx.Add(pre);
            cx.Add(pre.framing);
            lxr = new Lexer(cx, s, cx.db.lexeroffset, true);
            tok = lxr.tok;
            var b = cx.FixLl(pre.qMarks).First();
            for (; b != null && tok != Sqlx.EOF; b = b.Next())
                if (b.value() is long p)
                {
                    var v = lxr.val;
                    var lp = LexPos();
                    if (Match(Sqlx.DATE, Sqlx.TIME, Sqlx.TIMESTAMP, Sqlx.INTERVAL))
                    {
                        Sqlx tk = tok;
                        Next();
                        v = lxr.val;
                        if (tok == Sqlx.CHARLITERAL)
                        {
                            Next();
                            v = new SqlDateTimeLiteral(lp.dp, cx,
                                new Domain(lp.dp, tk, BTree<long, object>.Empty), v.ToString()).Eval(cx);
                        }
                    }
                    else
                        Mustbe(Sqlx.BLOBLITERAL, Sqlx.NUMERICLITERAL, Sqlx.REALLITERAL,
                            // Sqlx.DOCUMENTLITERAL,
                            Sqlx.CHARLITERAL, Sqlx.INTEGERLITERAL);
                    cx.values += (p, v);
                    Mustbe(Sqlx.SEMICOLON);
                }
            if (!(b == null && tok == Sqlx.EOF))
                throw new DBException("33001");
            cx.QParams(); // replace SqlLiterals that are QParams with actuals
            cx = pre.target?.Obey(cx)??cx;
            return cx.db;
        }
        /// <summary>
        ///SqlStatement =	Alter
        /// 	|	BEGIN TRANSACTION
        ///     |	Call
        ///     |	COMMIT [WORK]
        /// 	|	CreateClause
        /// 	|	CursorSpecification
        /// 	|	DeleteSearched
        /// 	|	DropClause
        /// 	|	Grant
        /// 	|	Insert
        /// 	|	Rename
        /// 	|	Revoke
        /// 	|	ROLLBACK [WORK]
        /// 	|	UpdateSearched .       
        /// </summary>
        /// <param name="rt">A Domain or ObInfo for the expected result of the Executable</param>
        /// <returns>The Executable result of the Parse</returns>
        public void ParseSqlStatement(Domain xp)
        {
            //            Match(Sqlx.RDF);
            switch (tok)
            {
                case Sqlx.ALTER: ParseAlter(); break;
                case Sqlx.CALL: ParseCallStatement(xp); break;
                case Sqlx.COMMIT:
                    Next();
                    if (Match(Sqlx.WORK))
                        Next();
                    if (cx.parse == ExecuteStatus.Obey)
                        cx.db.Commit(cx);
                    else
                        throw new DBException("2D000","Commit");
                    break;
                case Sqlx.CREATE: ParseCreateClause(); break;
                case Sqlx.DELETE: ParseSqlDelete(); break;
                case Sqlx.DROP: ParseDropStatement(); break;
                case Sqlx.GRANT: ParseGrant(); break; 
                case Sqlx.INSERT: ParseSqlInsert(); break;
                case Sqlx.MATCH: ParseSqlMatchStatement(); break;
                case Sqlx.REVOKE: ParseRevoke(); break;
                case Sqlx.ROLLBACK:
                    Next();
                    if (Match(Sqlx.WORK))
                        Next();
                    var e = new RollbackStatement(LexDp());
                    cx.exec = e;
                    if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                        cx = new Context(tr.Rollback(), cx.conn);
                    else
                        cx.Add(e);
                    cx.exec = e;
                    break;
                case Sqlx.SELECT: ParseCursorSpecification(xp); break;
                case Sqlx.SET: ParseSqlSet(); break;
                case Sqlx.TABLE: ParseCursorSpecification(xp); break;
                case Sqlx.UPDATE: ParseSqlUpdate(); break;
                case Sqlx.VALUES: ParseCursorSpecification(xp); break;
                //    case Sqlx.WITH: e = ParseCursorSpecification(); break;
                case Sqlx.EOF: break; // whole input is a comment
                default:
                    object ob = lxr.val;
                    if (ob == TNull.Value)
                        ob = new string(lxr.input, lxr.start, lxr.pos - lxr.start);
                    throw new DBException("42000", ob).ISO();
            }
        }
        byte MustBeLevelByte()
        {
            byte lv;
            if (tok == Sqlx.ID && lxr.val is TChar tc && tc.value.Length == 1)
                lv = (byte)('D' - tc.value[0]);
            else
                throw new DBException("4211A", tok.ToString());
            Next();
            return lv;
        }
        Level MustBeLevel()
        {
            Mustbe(Sqlx.LEVEL);
            var min = MustBeLevelByte();
            var max = min;
            if (tok == Sqlx.MINUS)
                max = MustBeLevelByte();
            var gps = BTree<string, bool>.Empty;
            if (tok==Sqlx.GROUPS)
            {
                Next();
                while (tok==Sqlx.ID && lxr.val!=null)
                {
                    gps +=(lxr.val.ToString(), true);
                    Next();
                }
            }
            var rfs = BTree<string, bool>.Empty;
            if (tok == Sqlx.REFERENCES)
            {
                Next();
                while (tok == Sqlx.ID && lxr.val!=null)
                {
                    rfs +=(lxr.val.ToString(), true);
                    Next();
                }
            }
            return new Level(min, max, gps, rfs);
        }
        /// <summary>
		/// Grant  = 	GRANT Privileges TO GranteeList [ WITH GRANT OPTION ] 
		/// |	GRANT Role_id { ',' Role_id } TO GranteeList [ WITH ADMIN OPTION ] 
        /// |   GRANT SECURITY Level TO User_id .
        /// </summary>
        void ParseGrant()
        {
            Next();
            if (Match(Sqlx.SECURITY))
            {
                Next();
                var lv = MustBeLevel();
                Mustbe(Sqlx.TO);
                var nm = lxr.val?.ToString()??throw new DBException("42135");
                Mustbe(Sqlx.ID);
                var usr = cx.db.objects[cx.db.roles[nm]??-1L] as User
                    ?? throw new DBException("42135", nm.ToString());
                if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.Add(new Clearance(usr.defpos, lv, tr.nextPos));
            }
            else if (Match(Sqlx.PASSWORD))
            {
                TypedValue pwd = new TChar("");
                Role? irole = null;
                Next();
                if (!Match(Sqlx.FOR) && !Match(Sqlx.TO))
                {
                    pwd = lxr.val;
                    Next();
                }
                if (Match(Sqlx.FOR))
                {
                    Next();
                    var rid = new Ident(this);
                    Mustbe(Sqlx.ID);
                    irole = cx.GetObject(rid.ident) as Role??
                        throw new DBException("42135", rid);
                }
                Mustbe(Sqlx.TO);
                var grantees = ParseGranteeList(Array.Empty<PrivNames>());
                if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                {
                    var irolepos = -1L;
                    if (irole != null && irole.name!=null)
                    {
                        tr.AccessRole(cx,true, new string[] { irole.name }, grantees, false);
                        irolepos = irole.defpos;
                    }
                    for (var i = 0; i < grantees.Length; i++)
                    {
                        var us = grantees[i];
                        cx.Add(new Authenticate(us.defpos, pwd.ToString(), irolepos,tr.nextPos));
                    }
                }
            }
            Match(Sqlx.OWNER, Sqlx.USAGE);
            if (Match(Sqlx.ALL, Sqlx.SELECT, Sqlx.INSERT, Sqlx.DELETE, Sqlx.UPDATE, Sqlx.REFERENCES, Sqlx.OWNER, Sqlx.TRIGGER, Sqlx.USAGE, Sqlx.EXECUTE))
            {
                var priv = ParsePrivileges();
                Mustbe(Sqlx.ON);
                var (ob,_) = ParseObjectName();
                long pob = ob?.defpos ?? 0;
                Mustbe(Sqlx.TO);
                var grantees = ParseGranteeList(priv);
                bool opt = ParseGrantOption();
                if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    tr.AccessObject(cx,true, priv, pob, grantees, opt);
            }
            else
            {
                var roles = ParseRoleNameList();
                Mustbe(Sqlx.TO);
                var grantees = ParseGranteeList(new PrivNames[] { new PrivNames(Sqlx.USAGE) });
                bool opt = ParseAdminOption();
                if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    tr.AccessRole(cx,true, roles, grantees, opt);
            }
        }
        /// <summary>
        /// ObjectName is used in GRANT and ALTER ROLE
		/// ObjectName = 	TABLE id
		/// 	|	DOMAIN id
		///     |	TYPE id
		///     |	Routine
		///     |	VIEW id 
        ///     |   ENTITY id .
        /// </summary>
        /// <param name="db">the connected database affected</param>
        /// <returns>the object that has been specified</returns>
		(DBObject,string) ParseObjectName()
        {
            Sqlx kind = Sqlx.TABLE;
            Match(Sqlx.INSTANCE, Sqlx.CONSTRUCTOR, Sqlx.OVERRIDING, Sqlx.VIEW, Sqlx.TYPE);
            if (tok != Sqlx.ID || lxr.val.ToString() == "INDEX")
            {
                kind = tok;
                Next();
                if (kind == Sqlx.INSTANCE || kind == Sqlx.STATIC || kind == Sqlx.OVERRIDING || kind == Sqlx.CONSTRUCTOR)
                    Mustbe(Sqlx.METHOD);
            }
            var n = lxr.val.ToString()??"?";
            Mustbe(Sqlx.ID);
            DBObject? ob=null;
            switch (kind)
            {
                case Sqlx.TABLE: 
                case Sqlx.DOMAIN:
                case Sqlx.VIEW:
                case Sqlx.ENTITY:
                case Sqlx.TYPE: ob = cx.GetObject(n) ??
                        throw new DBException("42135",n);
                    break;
                case Sqlx.CONSTRUCTOR: 
                case Sqlx.FUNCTION: 
                case Sqlx.PROCEDURE:
                    {
                        var a = ParseSignature();
                        ob = cx.GetProcedure(LexPos().dp,n, a)??
                            throw new DBException("42108",n);
                        break;
                    }
                case Sqlx.INSTANCE: 
                case Sqlx.STATIC: 
                case Sqlx.OVERRIDING: 
                case Sqlx.METHOD:
                    {
                        var a = ParseSignature();
                        Mustbe(Sqlx.FOR);
                        var tp = lxr.val.ToString();
                        Mustbe(Sqlx.ID);
                        var oi = ((cx.role.dbobjects[tp] is long p)?cx._Ob(p):null)?.infos[cx.role.defpos] ??
                            throw new DBException("42119", tp);
                        ob = (DBObject?)oi.mem[oi.methodInfos[n]?[a]??-1L]??
                            throw new DBException("42108",n);
                        break;
                    }
                case Sqlx.TRIGGER:
                    {
                        Mustbe(Sqlx.ON);
                        var tn = lxr.val.ToString();
                        Mustbe(Sqlx.ID);
                        var tb = cx.GetObject(tn) as Table?? throw new DBException("42135", tn);
                        for (var b = tb.triggers.First(); ob == null && b != null; b = b.Next())
                            for (var c = b.value().First(); ob == null && c != null; c = c.Next())
                                if (cx._Ob(c.key()) is Trigger tg && tg.name == n)
                                    ob = tg;
                        if (ob==null)
                            throw new DBException("42107", n);
                        break;
                    }
                case Sqlx.DATABASE:
                    ob = SqlNull.Value;
                    break;
                default:
                    throw new DBException("42115", kind).Mix();
            }
            if (ob == null) throw new PEException("00083");
            return (cx.Add(ob),n);
        }
        /// <summary>
        /// used in ObjectName. 
        /// '('Type, {',' Type }')'
        /// </summary>
        /// <returns>the number of parameters</returns>
		CList<Domain> ParseSignature()
        {
            CList<Domain> fs = CList<Domain>.Empty;
            if (tok == Sqlx.LPAREN)
            {
                Next();
                if (tok == Sqlx.RPAREN)
                {
                    Next();
                    return CList<Domain>.Empty;
                }
                fs += ParseSqlDataType();
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    fs += ParseSqlDataType();
                }
                Mustbe(Sqlx.RPAREN);
            }
            return fs;
        }
        /// <summary>
		/// ObjectPrivileges = ALL PRIVILEGES | Action { ',' Action } .
        /// </summary>
        /// <returns>The list of privileges</returns>
		PrivNames[] ParsePrivileges()
        {
            var r = new List<PrivNames>();
            if (tok == Sqlx.ALL)
            {
                Next();
                Mustbe(Sqlx.PRIVILEGES);
                return Array.Empty<PrivNames>();
            }
            r.Add(ParsePrivilege());
            while (tok == Sqlx.COMMA)
            {
                Next();
                r.Add(ParsePrivilege());
            }
            return r.ToArray();
        }
        /// <summary>
		/// Action = 	SELECT [ '(' id { ',' id } ')' ]
		/// 	|	DELETE
		/// 	|	INSERT  [ '(' id { ',' id } ')' ]
		/// 	|	UPDATE  [ '(' id { ',' id } ')' ]
		/// 	|	REFERENCES  [ '(' id { ',' id } ')' ]
		/// 	|	USAGE
        /// 	|   TRIGGER
		/// 	|	EXECUTE 
        /// 	|   OWNER .
        /// </summary>
        /// <returns>A singleton privilege (list of one item)</returns>
		PrivNames ParsePrivilege()
        {
            var r = new PrivNames(tok);
            Mustbe(Sqlx.SELECT, Sqlx.DELETE, Sqlx.INSERT, Sqlx.UPDATE,
                Sqlx.REFERENCES, Sqlx.USAGE, Sqlx.TRIGGER, Sqlx.EXECUTE, Sqlx.OWNER);
            if ((r.priv == Sqlx.UPDATE || r.priv == Sqlx.REFERENCES || r.priv == Sqlx.SELECT || r.priv == Sqlx.INSERT) && tok == Sqlx.LPAREN)
            {
                Next();
                r.cols += (lxr.val.ToString(),true);
                Mustbe(Sqlx.ID);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    r.cols += (lxr.val.ToString(),true);
                    Mustbe(Sqlx.ID);
                }
                Mustbe(Sqlx.RPAREN);
            }
            return r;
        }
        /// <summary>
		/// GranteeList = PUBLIC | Grantee { ',' Grantee } .
        /// </summary>
        /// <param name="priv">the list of privieges to grant</param>
        /// <returns>the updated database objects</returns>
		DBObject[] ParseGranteeList(PrivNames[] priv)
        {
            var r = new List<DBObject>
            {
                ParseGrantee(priv)
            };
            while (tok == Sqlx.COMMA)
            {
                Next();
                r.Add(ParseGrantee(priv));
            }
            return r.ToArray();
        }
        /// <summary>
        /// helper for non-reserved words
        /// </summary>
        /// <returns>if we match a method mode</returns>
        bool MethodModes()
        {
            return Match(Sqlx.INSTANCE, Sqlx.OVERRIDING, Sqlx.CONSTRUCTOR);
        }
        internal Role? GetRole(string n)
        {
            return (Role?)cx.db.objects[cx.db.roles[n]??-1L];
        }
        /// <summary>
		/// Grantee = 	[USER] id
		/// 	|	ROLE id . 
        /// </summary>
        /// <param name="priv">the list of privileges</param>
        /// <returns>the updated grantee</returns>
		DBObject ParseGrantee(PrivNames[] priv)
        {
            Sqlx kind = Sqlx.USER;
            if (Match(Sqlx.PUBLIC))
            {
                Next();
                return (Role?)cx.db.objects[Database.Guest]??throw new PEException("PE2400");
            }
            if (Match(Sqlx.USER))
                Next();
            else if (Match(Sqlx.ROLE))
            {
                kind = Sqlx.ROLE;
                Next();
            }
            var n = lxr.val.ToString();
            Mustbe(Sqlx.ID);
            DBObject? ob;
            switch (kind)
            {
                case Sqlx.USER:
                    {
                        ob = GetRole(n);
                        if ((ob == null || ob.defpos == -1) && cx.db is Transaction tr)
                            ob = cx.Add(new PUser(n, tr.nextPos, cx));
                        break;
                    }
                case Sqlx.ROLE: 
                    {
                        ob = GetRole(n)??throw new DBException("28102",n);
                        if (ob.defpos>=0)
                        { // if not PUBLIC we need to have privilege to change the grantee role
                            var ri = ob.infos[cx.role.defpos];
                            if (ri == null || !ri.priv.HasFlag(Role.admin))
                                throw new DBException("42105");
                        }
                    }
                    break;
                default: throw new DBException("28101").Mix();
            }
            if (ob == SqlNull.Value && (priv == null || priv.Length != 1 || priv[0].priv != Sqlx.OWNER))
                throw new DBException("28102", kind, n).Mix();
            if (ob == null)
                throw new PEException("PE2401");
            return cx.Add(ob);
        }
        /// <summary>
        /// [ WITH GRANT OPTION ] 
        /// </summary>
        /// <returns>whether WITH GRANT OPTION was specified</returns>
		bool ParseGrantOption()
        {
            if (tok == Sqlx.WITH)
            {
                Next();
                Mustbe(Sqlx.GRANT);
                Mustbe(Sqlx.OPTION);
                return true;
            }
            return false;
        }
        /// <summary>
        /// [ WITH ADMIN OPTION ] 
        /// </summary>
        /// <returns>whether WITH ADMIN OPTION was specified</returns>
		bool ParseAdminOption()
        {
            if (tok == Sqlx.WITH)
            {
                Next();
                Mustbe(Sqlx.ADMIN);
                Mustbe(Sqlx.OPTION);
                return true;
            }
            return false;
        }
        /// <summary>
        /// Role_id { ',' Role_id }
        /// </summary>
        /// <returns>The list of Roles</returns>
		string[] ParseRoleNameList()
        {
            var r = new List<string>();
            if (tok == Sqlx.ID)
            {
                r.Add(lxr.val.ToString());
                Next();
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    r.Add(lxr.val.ToString());
                    Mustbe(Sqlx.ID);
                }
            }
            return r.ToArray();
        }
        /// <summary>
		/// Revoke = 	REVOKE [GRANT OPTION FOR] Privileges FROM GranteeList
		/// 	|	REVOKE [ADMIN OPTION FOR] Role_id { ',' Role_id } FROM GranteeList .
        /// Privileges = ObjectPrivileges ON ObjectName .
        /// </summary>
        /// <returns>the executable</returns>
		Executable? ParseRevoke()
        {
            Next();
            Sqlx opt = ParseRevokeOption();
            if (tok == Sqlx.ID)
            {
                var priv = ParseRoleNameList();
                Mustbe(Sqlx.FROM);
                var grantees = ParseGranteeList(Array.Empty<PrivNames>());
                if (opt == Sqlx.GRANT)
                    throw new DBException("42116").Mix();
                if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    tr.AccessRole(cx,false, priv, grantees, opt == Sqlx.ADMIN);
            }
            else
            {
                if (opt == Sqlx.ADMIN)
                    throw new DBException("42117").Mix();
                var priv = ParsePrivileges();
                Mustbe(Sqlx.ON);
                var (ob,_) = ParseObjectName();
                Mustbe(Sqlx.FROM);
                var grantees = ParseGranteeList(priv);
                if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    tr.AccessObject(cx,false, priv, ob.defpos, grantees, (opt == Sqlx.GRANT));
            }
            return null;
        }
        /// <summary>
        /// [GRANT OPTION FOR] | [ADMIN OPTION FOR]
        /// </summary>
        /// <returns>GRANT or ADMIN or NONE</returns>
		Sqlx ParseRevokeOption()
        {
            Sqlx r = Sqlx.NONE;
            if (Match(Sqlx.GRANT, Sqlx.ADMIN))
            {
                r = tok;
                Next();
                Mustbe(Sqlx.OPTION);
                Mustbe(Sqlx.FOR);
            }
            return r;
        }
        /// <summary>
		/// Call = 		CALL Procedure_id '(' [  TypedValue { ','  TypedValue } ] ')' 
		/// 	|	MethodCall .
        /// </summary>
        /// <param name="ob">The target object of the method call if any</param>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseCallStatement(Domain xp)
        {
            Next();
            Executable e = ParseProcedureCall(xp);
            if (cx.parse != ExecuteStatus.Parse && cx.db is Transaction tr)
                cx = tr.Execute(e,cx);
            return (Executable)cx.Add(e);
        }
        /// <summary>
		/// Create =	CREATE ROLE id [string]
		/// |	CREATE DOMAIN id [AS] DomainDefinition {Metadata}
		/// |	CREATE FUNCTION id '(' Parameters ')' RETURNS Type Body 
        /// |   CREATE ORDERING FOR id Order
		/// |	CREATE PROCEDURE id '(' Parameters ')' Body
		/// |	CREATE Method Body
		/// |	CREATE TABLE id TableContents [UriType] {Metadata}
		/// |	CREATE TRIGGER id (BEFORE|AFTER) Event ON id [ RefObj ] Trigger
		/// |	CREATE TYPE id [UNDER id] AS Representation [ Method {',' Method} ] {Metadata}
		/// |	CREATE ViewDefinition 
        /// |   CREATE XMLNAMESPACES NamespaceList
        /// |   CREATE (Node) {-[Edge]->(Node)|<-[Edge]-(Node)}
        /// </summary>
        /// <returns>the executable</returns>
		void ParseCreateClause()
        {
            if (cx.role is Role dr  
                && dr.infos[cx.role.defpos]?.priv.HasFlag(Grant.Privilege.AdminRole)!=true
                && dr.defpos!= -502)
                throw new DBException("42105");
            Next();
            MethodModes();
            Match(Sqlx.TEMPORARY, Sqlx.ROLE, Sqlx.VIEW, Sqlx.TYPE, Sqlx.DOMAIN);
            if (Match(Sqlx.ORDERING))
                ParseCreateOrdering();
            else if (Match(Sqlx.XMLNAMESPACES))
                ParseCreateXmlNamespaces();
            else if (tok == Sqlx.PROCEDURE || tok == Sqlx.FUNCTION)
            {
                bool func = tok == Sqlx.FUNCTION;
                Next();
                ParseProcedureClause(func, Sqlx.CREATE);
            }
            else if (Match(Sqlx.OVERRIDING, Sqlx.INSTANCE, Sqlx.STATIC, Sqlx.CONSTRUCTOR, Sqlx.METHOD))
                ParseMethodDefinition();
            else if (tok == Sqlx.TABLE || tok == Sqlx.TEMPORARY)
            {
                if (tok == Sqlx.TEMPORARY)
                {
                    Role au = GetRole("Temp") ?? throw new DBException("3D001", "Temp").Mix();
                    cx = new Context(cx, au, cx.db.user ?? throw new DBException("42105"));
                    Next();
                }
                Mustbe(Sqlx.TABLE);
                ParseCreateTable();
            }
            else if (tok == Sqlx.TRIGGER)
            {
                Next();
                ParseTriggerDefClause();
            }
            else if (tok == Sqlx.DOMAIN)
            {
                Next();
                ParseDomainDefinition();
            }
            else if (tok == Sqlx.TYPE)
            {
                Next();
                ParseTypeClause();
            }
            else if (tok == Sqlx.ROLE)
            {
                Next();
                var id = lxr.val.ToString();
                Mustbe(Sqlx.ID);
                TypedValue o = new TChar("");
                if (Match(Sqlx.CHARLITERAL))
                {
                    o = lxr.val;
                    Next();
                }
                cx.Add(new PRole(id, o.ToString(), cx.db.nextPos, cx));
            }
            else if (tok == Sqlx.VIEW)
                ParseViewDefinition();
            else if (tok == Sqlx.LPAREN)
                ParseCreateGraph();
            else
                throw new DBException("42118", tok).Mix();
        }
        /// <summary>
        /// A graph fragment can be supplied in a CREATE statement using syntax
        /// adopted from neo4j. It results in the addition of at least one Record
        /// in a node type: the node type definition comprising a table and UDT may 
        /// be created and/or altered on the fly, and may extend to further edge and edge types.
        /// The given input is a sequence of clauses that contain graph expressions
        /// with internal and external references to existing and new nodes, edges and their types.
        /// Values are not always constants either, so the graph expressions must be
        /// SqlValueGraph rather than TGraphs.
        /// This routine 
        /// 1. constructs a list of SqlValueGraphs corresponding to the given input, 
        /// with their internal and external references. Many of the node and edge references
        /// will be unbound at this stage because some types may be new or to be modified, and
        /// expressions have not been evaluated.
        /// 2. analyses the referenced types and expressions for consistency. (recursive, semi-interactive)
        /// 3. generates Physicals to update the set of types.
        /// 4. evaluates the SqlValueGraphs to generate Records and Updates (recursive)
        ///    Binds the (uncomitted) nodes
        /// 5. adds the TGraph and Node->Edge associations to the database
        /// </summary>
        internal void ParseCreateGraph()
        {
            // New nodes without ID keys should be assigned cx.db.nextPos.ToString(), and this is fixed
            // on Commit, see Record(Record,Writer): the NodeOrEdge flag is added in Record()
            var svgs = ParseSqlGraphList();
        }
        CTree<long,SqlRow> ParseSqlGraphList()
        {
            var svgs = CTree<long,SqlRow>.Empty;
            // the current token is LPAREN
            while (tok==Sqlx.LPAREN)
            {
                svgs += ParseSqlGraph();
                if (tok==Sqlx.COMMA)
                    Next();
            };
            return svgs;
        }
        CTree<long, SqlRow> ParseSqlGraph()
        {
            // the current token is LPAREN
            var svgs = CTree<long,SqlRow>.Empty;
            string n;
            (n,svgs)= ParseNodeOrEdge(svgs, Domain.NodeType);
            while (tok==Sqlx.RARROW || tok == Sqlx.ARROWBASE)
                (n,svgs) = ParseNodeOrEdge(svgs,Domain.EdgeType,n);

            return svgs;
        }
        /// <summary>
        /// Node and edge syntax is slightly different!
        /// For a node, we expect and identifier part followed optionally by a properties part.
        /// The identifier part can be a single identifier(case N0), 
        /// a sequence of identifiers separated by colons (case N1), or 
        /// such a sequence preceded by a colon (case N2).
        /// Having seen the opening -[ or <-[ for an Edge, we expect one or more identifiers (if mode than one,
        /// they are separated by a colon (case E1), or if only one, it is preceded by a colon (case E2)) 
        /// followed by an optional document literal and then we must see the corresponding ]-> or ]- token.
        /// All identifiers following the colon are (possibly new) node/edge types.
        /// If an identifier precedes the colon (case N0,E1), it is a value for the node or edge id, 
        /// otherwise a default id will be supplied: 
        ///     in case E1, this id should not already exist
        ///     in case N1, if it already exists it should not be followed by a colon.
        /// During the process of building the graph fragments. we also generate cx.db.physicals:
        /// any changes to previous fragments should also modify the corresponding cx.db.physicals!
        /// </summary>
        /// <param name="svgs">The graph fragments so far</param>
        /// <param name="dt">The standard NODETYPE or EDGETYPE</param>
        /// <param name="ln">The name of the node to attach the new edge</param>
        /// <returns>The name of the new node or edge and the list of all the graph fragments</returns>
        (string, CTree<long, SqlRow>) ParseNodeOrEdge(CTree<long, SqlRow> svgs, UDType dt, string? ln = null)
        {
            var ab = tok;
            Next();
            var b = new Ident(this);
            if (tok == Sqlx.ID)
                Next();
            if (b.ident == "COLON") // generate a new ID
            {
                var np = cx.db.nextPos;
                b = new Ident(np.ToString(), new Iix(b.iix, np));
                cx.db += (Database.NextPos, np + 1);
            }
            if (tok != Sqlx.COLON) // Must be case N0: b must be known to the transaction
            {
                Mustbe(Sqlx.RPAREN);
                if (cx.db.nodeIds[b.ident] is null)
                    throw new DBException("42161", "Node", b.ident);
                return (b.ident, svgs);
            }
            // At this point, b is a node or edge ID
            Next();
            var a = new Ident(this);  // A node or edge type
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.COLON && ln == null) // a further colon indicates a subtype
            {
                Next();
                var c = new Ident(this);
                Mustbe(Sqlx.ID);
                if (cx.role.dbobjects[c.ToString()] is long np && cx._Dom(np) is NodeType ct)
                    dt = ct;
                else
                {
                    var l0 = BTree<string, SqlValue>.Empty;
                    var wt = (dt is EdgeType) ? BuildEdgeType(cx.db.nextPos, c, l0, (dt.defpos < 0) ? -1L : dt.structure)
                                        : BuildNodeType(cx.db.nextPos, c, l0, (dt.defpos < 0) ? -1L : dt.structure);
                    dt = (NodeType)(cx.Add(wt) ??
                        throw new PEException("PE91210"));
                    cx.db += (Database.NextStmt, cx.db.nextStmt + 1);
                    cx.Modify(dt); // adjust supertype hierarchy
                }
            }
            var ls = CTree<string, SqlValue>.Empty;
            if (tok == Sqlx.LBRACE)
            {
                Next();
                if (tok != Sqlx.RBRACE)
                    ls += GetDocItem(cx);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    ls += GetDocItem(cx);
                }
                Mustbe(Sqlx.RBRACE);
            }
            ls += ("ID", (SqlValue)cx.Add(new SqlLiteral(b.iix.dp, new TChar(b.ToString()))));
            var ll = BList<DBObject>.Empty;
            if (ln == null)
            {
                Mustbe(Sqlx.RPAREN);
                NodeType nt;
                if (cx.role.dbobjects[a.ident] is long np && cx._Ob(np) is NodeType nodetype)
                    nt = CheckNodeType(nodetype, ls);
                else // create a new NodeType
                    nt = BuildNodeType(cx.db.nextPos, a, ls, (dt.defpos < 0) ? -1L : dt.structure); // dt is supertype
                var t = new TableRowSet(cx.GetUid(), cx, nt.structure);
                cx.Add(t);
                var dm = cx._Dom(t) ?? throw new DBException("42105");
                var vp = cx.GetUid();
                for (var bb = cx._Dom(nt)?.rowType.First(); bb != null; bb = bb.Next())
                    ll += ls[cx.NameFor(bb.value() ?? -1L)] ?? SqlNull.Value;
                var rn = new SqlRow(cx.GetUid(), cx, ll) + (Table._NodeType, nt.defpos);
                cx.Add(rn);
                // carefully construct what would happen with ordinary SQL INSERT VALUES
                SqlValue n = rn;
                if (cx._Dom(n) is Domain dv) // tolerate a single value without the VALUES keyword
                    n = new SqlRowArray(vp, cx, dv, new BList<long?>(n.defpos));
                var sce = n.RowSetFor(vp, cx, dm.rowType, dm.representation) + (RowSet.RSTargets, t.rsTargets)
                    + (RowSet.Asserts, RowSet.Assertions.AssignTarget);
                var s = new SqlInsert(cx.GetUid(), t, sce.defpos, dm);
                cx.Add(s);
                s.Obey(cx);
                cx.defs += (b.ident, b.iix, Ident.Idents.Empty);
                if (cx._Ob(nt.structure) is Table ta && ta.tableRows[ta.lastData] is TableRow ra)
                {
                    if (nt is EdgeType er)
                        cx.db += new TEdge(ra.defpos,er,ra.vals);
                    else
                        cx.db += new TNode(ra.defpos,nt,ra.vals);
                }
                return (b.ident, svgs + (rn.defpos, rn));
            }
            Mustbe(Sqlx.ARROW, Sqlx.RARROWBASE);
            string an;
            (an, svgs) = ParseNodeOrEdge(svgs, Domain.NodeType);
            var lp = (ab == Sqlx.ARROWBASE) ? ln : an;
            var ap = (ab == Sqlx.ARROWBASE) ? an : ln;
            ls += ("LEAVING", (SqlValue)cx.Add(new SqlLiteral(cx.GetUid(), new TChar(lp))));
            ls += ("ARRIVING", (SqlValue)cx.Add(new SqlLiteral(cx.GetUid(), new TChar(ap))));
            EdgeType et;
            if (cx.role.dbobjects[a.ident] is long p && cx._Ob(p) is EdgeType edgetype)
                et = CheckEdgeType(edgetype, ls);
            else // create a new EdgeType
                et = BuildEdgeType(cx.db.nextPos, a, ls, (dt.defpos < 0) ? -1L : dt.structure);
            var ts = new TableRowSet(cx.GetUid(), cx, et.structure);
            cx.Add(ts);
            var de = cx._Dom(et) ?? throw new DBException("42105");
            var ve = cx.GetUid();
            ll = BList<DBObject>.Empty;
            for (var bb = cx._Dom(et)?.rowType.First(); bb != null; bb = bb.Next())
                ll += ls[cx.NameFor(bb.value() ?? -1L)] ?? SqlNull.Value;
            var re = new SqlRow(cx.GetUid(), cx, ll) + (Table._NodeType, et.defpos);
            cx.Add(re);
            // carefully construct what would happen with ordinary SQL INSERT VALUES
            SqlValue e = re;
            if (cx._Dom(e) is Domain ed) // tolerate a single value without the VALUES keyword
                e = new SqlRowArray(ve, cx, ed, new BList<long?>(e.defpos));
            var rs = e.RowSetFor(ve, cx, de.rowType, de.representation) + (RowSet.RSTargets, ts.rsTargets)
                + (RowSet.Asserts, RowSet.Assertions.AssignTarget);
            var si = new SqlInsert(cx.GetUid(), ts, rs.defpos, cx._Dom(ts) ?? Domain.Null);
            cx.Add(si);
            si.Obey(cx);
            if (cx._Ob(et.structure) is Table tb && tb.tableRows[tb.lastData] is TableRow tr)
                cx.db += new TEdge(tr.defpos,et,tr.vals);
            return (an, svgs);
        }
        internal void ParseSqlMatchStatement()
        {
            Next();
            var (gs, svgs) = ParseSqlMatchList();
            for (var b = gs.First(); b != null; b = b.Next())
                if (b.value() is TGParam g && g.id!="_")
                {
                    var ix = new Iix(g.uid);
                    var id = new Ident(g.id[1..], ix);
                    cx.Add(new SqlValue(id, Domain.Char));
                    cx.defs += (id, ix);
                }
            var wh = ParseWhereClause() ?? CTree<long, bool>.Empty;
            var e = (ParseProcedureStatement(Domain.Content) is Executable ex) ? ex.defpos : -1L;
            var ms = (MatchStatement)cx.Add(new MatchStatement(cx.GetUid(), svgs, gs, wh, e));
            if (cx.parse == ExecuteStatus.Obey)
                ms.Obey(cx);
        }
        (CTree<long,TGParam>, CTree<TGraph, bool>) ParseSqlMatchList()
        {
            var ids = CTree<long, TGParam>.Empty;
            var set = CTree<TGraph, bool>.Empty;
            // the current token is LPAREN
            while (tok == Sqlx.LPAREN)
            {
                (ids, var ns) = ParseSqlMatch(ids, CTree<TNode, bool>.Empty);
                if (tok == Sqlx.COMMA)
                    Next();
                var nu = CTree<long, TNode>.Empty;
                var ni = CTree<string, TNode>.Empty;
                for (var b = ns.First(); b != null; b = b.Next())
                    if (b.key() is TNode n)
                    {
                        nu += (n.uid, n);
                        ni += (n.id, n);
                    }
                set += (new TGraph(nu, ni),true);
            };
            return (ids, set);
        }
        (CTree<long, TGParam>, CTree<TNode,bool>) 
            ParseSqlMatch(CTree<long, TGParam> ids, CTree<TNode,bool> ns)
        {
            // the current token is LPAREN
            (var n, ids, ns) = ParseMatchNodeOrEdge(LexPos().dp, ids, ns, Domain.NodeType);
            while (tok == Sqlx.RARROW || tok == Sqlx.ARROWBASE)
                (n, ids, ns) = ParseMatchNodeOrEdge(LexPos().dp, ids, ns, Domain.EdgeType, n);
            return (ids,ns);
        }
        /// <summary>
        /// Node and edge syntax is slightly different for MATCH!
        /// Identifiers beginning with _ are initially unbound: we have a list of such identifiers.
        /// For a node, we expect a colon-separated identifier chain optionally followed by a partial property list
        /// Having seen the opening -[ or <-[ for an Edge, we expect one or two identifiers (if mode than one,
        /// they are separated by a colon (case E1), or if only one, it is preceded by a colon (case E2)) 
        /// followed by an optional document literal and then we must see the corresponding ]-> or ]- token.
        /// All identifiers following the colon are (possibly new) node/edge types.
        /// If an identifier precedes the colon (case N0,E1), it is a value for the node or edge id, 
        /// otherwise we don't care.
        /// Constraint: string is a property id for the node instance, or one of 
        /// the following specific strings with leading spaces: " ID", " LEAVING", " ARRIVING", " SPECIFICTYPE". 
        /// Anything more complicated needs to be in the where condition.
        /// Subtypes cannot be specified during match.
        /// </summary>
        /// <param name="ids">The unbound identifiers found so far with their occurrence positions</param>
        /// <param name="set">The constraints so far</param>
        /// <param name="dt">The standard NODETYPE or EDGETYPE</param>
        /// <param name="ln">The name of the preceding node</param>
        /// <returns>TNull if no match is possible, otherwise the new node or edge and the list of all the graph fragments</returns>
        (TMatch, CTree<long, TGParam>, CTree<TNode, bool>)
            ParseMatchNodeOrEdge(long dp, CTree<long, TGParam> ids, CTree<TNode, bool> set, NodeType dt, TMatch? ln = null)
        {
            var ab = tok;
            // we prepare the first item of the return value (just now, could be anything)
            // Recall it is a syntax error to add any conditions to _, or to add conditions to an unbound
            // identifier that has already appeared. But we don't add the unbound node id until
            // the end of its definition.
            // We are here because we have just seen LPAREN, ARROWBASE or RARROW
            Next();
            TMatch? r = null;
            var b = lxr.val; // The node name: must be TGParam or TChar
            TypedValue? a = null;
            if (tok == Sqlx.NODE) // b is TGParam
                Next();
            else if (tok == Sqlx.COLON)
            {
                var tg = new TGParam(LexPos().dp, "_", ab,
                        Domain.Content, CTree<string, TypedValue>.Empty);
                lxr.tgs += (tg.uid,tg);
                b = tg;
            }
            else
                Mustbe(Sqlx.ID);
            if (tok == Sqlx.COLON)
            {
                Next();
                a = lxr.val;  // A node or edge type
                if (r is TNode n)
                    throw new DBException("42000");
                if (b.ToString().StartsWith('_') && cx.role.dbobjects[a.ToString()] is long p)
                    dt = (NodeType)(cx.db.objects[p] ?? Domain.EdgeType);
                Next();
            }
            var c = CTree<string, TypedValue>.Empty;
            if (a != null)
                c += ("SPECIFICTYPE", a);
            if (tok == Sqlx.LBRACE)
            {
                if (r is TNode n)
                    throw new DBException("42000");
                Next();
                if (tok != Sqlx.RBRACE)
                    c = GetDocItem(c);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    c = GetDocItem(c);
                }
                Mustbe(Sqlx.RBRACE);
            }
            if (ln== null || dt.kind!=Sqlx.EDGETYPE)
            {
                Mustbe(Sqlx.RPAREN);
                r = new TMatch(cx.GetUid(), b.ToString(), dt, c, lxr);
                set += (r, true);
                ids += lxr.tgs;
                lxr.tgs = CTree<long, TGParam>.Empty;
                return (r, ids, set);
            }
            // if we get here, we are adding edge r
            Mustbe(Sqlx.ARROW, Sqlx.RARROWBASE);
            var ep = cx.GetUid();
            TMatch an;
            var ot = lxr.tgs;
            lxr.tgs = CTree<long, TGParam>.Empty;
            (an, ids, set) = ParseMatchNodeOrEdge(dp, ids, set, Domain.NodeType, null);
            lxr.tgs = ot;
            var lo = (ab == Sqlx.ARROWBASE) ? ln : an;
            var ao = (ab == Sqlx.ARROWBASE) ? an : ln;
            c = c + ("LEAVING", lo) + ("ARRIVING", ao);
            if (b.ToString() != "_" && b.ToString().StartsWith('_'))
            {
                var tg = new TGParam(cx.GetUid(), b.ToString(), ab, dt, c);
                lxr.tgs += (tg.uid, tg);
            }
            r = new TMatch(ep, b.ToString(), dt, c, lxr);
            set += (r, true);
            ids += lxr.tgs;
            lxr.tgs = CTree<long,TGParam>.Empty;
            return (r, ids, set);
        }
        /// <summary>
        /// GET option here is Pyrrho shortcut, needs third syntax for ViewDefinition
        /// ViewDefinition = id [ViewSpecification] AS (QueryExpression|GET) {TableMetadata} .
        /// ViewSpecification = Cols 
        ///       | OF id 
        ///       | OF '(' id Type {',' id Type} ')' .
        /// </summary>
        /// <returns>the executable</returns>
        internal RowSet? ParseViewDefinition(string? id = null)
        {
            var op = cx.parse;
            var lp = LexPos();
            var sl = lxr.start;
            if (id == null)
            {
                Next();
                id = lxr.val.ToString();
                Mustbe(Sqlx.ID);
                if (cx.db.role.dbobjects.Contains(id) == true)
                    throw new DBException("42104", id);
            }
            // CREATE VIEW always creates a new Compiled object,
            // whose columns and datatype are recorded in the framing part.
            // For a normal view the columns are SqlCopies that refer to a derived table
            // to be defined in the AS CursorSpecification part of the syntax:
            // so that initially they will have the undefined Content datatype.
            // If it is a RestView the column datatypes are specified inline
            // and constitute a VirtualTable which will have a defining position
            // and maybe have associated VirtualIndexes also with defining positions.
            // In all cases there will be objects defined in the Framing: 
            // these accumulate naturally during parsing.
            // The usage of these framing objects is different:
            // normal views are always instanced, while restviews are not.
            Domain dm = Domain.TableType;
            cx.defs = Ident.Idents.Empty;
            var nst = cx.db.nextStmt;
            /*        BList<Physical> rest = null;  // if rest, virtual table and indexes */
            Table? us = null;  // For the USING table of a RestViewUsing
            var ts = BTree<long, ObInfo>.Empty;
            if (Match(Sqlx.LPAREN))
            {
                Next();
                for (var i = 0; ; i++)
                {
                    var n = lxr.val.ToString();
                    var np = LexPos();
                    Mustbe(Sqlx.ID);
                    ts += (np.dp, new ObInfo(n));
                    if (Mustbe(Sqlx.COMMA, Sqlx.RPAREN) == Sqlx.RPAREN)
                        break;
                }
            }
            else if (Match(Sqlx.OF))
            {
                cx.parse = ExecuteStatus.Compile;
                Next();
                lp = LexPos();
                sl = lxr.start;
                if (Match(Sqlx.LPAREN)) // inline type def (RestView only)
                {
                    dm = (Domain)cx.Add(ParseRowTypeSpec(Sqlx.VIEW));
                    /*                   var vn = new string(lxr.input, sl, lxr.input.Length - sl-1);
                                       var x = vn.LastIndexOf(")");
                                       vn = vn.Substring(0, x+1);
                                       var pt = new PTable(vn, new Domain(cx.GetUid(), cx, Sqlx.VIEW, BList<SqlValue>.Empty), 
                                           cx.db.nextPos, cx);
                                       rest += pt;
                                       (dm,rest) = ParseRestViewSpec(pt.ppos,rest); */
                }
                else
                {
                    var tn = lxr.val.ToString();
                    Mustbe(Sqlx.ID);
                    dm = ((cx.db.role.dbobjects[tn] is long p) ? cx._Dom(p) : null) ??
                        throw new DBException("42119", tn, "").Mix();
                }
                cx.parse = op;
            }
            Mustbe(Sqlx.AS);
            var rest = Match(Sqlx.GET);
            RowSet? ur = null;
            RowSet? cs = null;
            if (!rest)
            {
                cx.parse = ExecuteStatus.Compile;
                cs = _ParseCursorSpecification(Domain.TableType);
                if (ts != BTree<long, ObInfo>.Empty)
                {
                    var ub = cx._Dom(cs)?.rowType.First();
                    for (var b = ts.First(); b != null && ub != null; b = b.Next(), ub = ub.Next())
                        if (ub.value() is long u && cx.obs[u] is SqlValue v && b.value().name is string nn)
                            cx.Add(v + (DBObject._Alias, nn));
                }
                cx.Add(new SelectStatement(cx.GetUid(), cs));
                cs = (RowSet?)cx.obs[cs.defpos] ?? throw new PEException("PE1802");
                var d = (Domain?)cx.obs[cs.domain] ?? Domain.Content;
                var nb = ts.First();
                for (var b = d.rowType.First(); b != null; b = b.Next(), nb = nb?.Next())
                    if (b.value() is long p && cx.obs[p] is SqlValue v)
                    {
                        if ((cx._Dom(v) ?? Domain.Content).kind == Sqlx.CONTENT || v.defpos < 0) // can't simply use WellDefined
                            throw new DBException("42112", v.NameFor(cx));
                        if (nb != null && nb.value().name is string bn)
                            cx.Add(v + (DBObject._Alias, bn));
                    }
                for (var b = cx.forReview.First(); b != null; b = b.Next())
                    if (cx.obs[b.key()] is SqlValue k)
                        for (var c = b.value().First(); c != null; c = c.Next())
                            if (cx.obs[c.key()] is RowSet rs && rs is not SelectRowSet)
                                rs.Apply(new BTree<long, object>(RowSet._Where, new CTree<long, bool>(k.defpos, true)), cx);
                cx.parse = op;
            }
            else
            {
                Next();
                if (tok == Sqlx.USING)
                {
                    op = cx.parse;
                    cx.parse = ExecuteStatus.Compile;
                    Next();
                    ur = ParseTableReferenceItem(lp.lp);
                    us = (Table?)cx.obs[ur.target] ?? throw new DBException("42107");
                    cx.parse = op;
                }
            }
            PView? pv;
            View? vw = null;
            if (rest)
            {
                if (us == null)
                    pv = new PRestView(id, dm.structure, dm, nst, cx.db.nextPos, cx);
                else
                    pv = new PRestView2(id, dm.structure, dm, nst,
                        ur ?? throw new PEException("PE2500"),
                        cx.db.nextPos, cx);
            }
            else
            {
                cx.Add(cs ?? throw new DBException("22204"));
                pv = new PView(id, new string(lxr.input, sl, lxr.pos - sl),
                    cx._Dom(cs) ?? throw new DBException("22204"), nst,
                    cx.db.nextPos, cx);
            }
            pv.framing = new Framing(cx, nst);
            vw = (View)(cx.Add(pv) ?? throw new DBException("42105"));
            if (StartMetadata(Sqlx.VIEW))
            {
                var m = ParseMetadata(Sqlx.VIEW);
                if (vw != null && m != null)
                    cx.Add(new PMetadata(id, -1, vw, m, cx.db.nextPos));
            }
            cx.result = -1L;
            return cs; // is null for PRestViews
        }
        /// <summary>
        /// Parse the CreateXmlNamespaces syntax
        /// </summary>
        /// <returns>the executable</returns>
        private Executable ParseCreateXmlNamespaces()
        {
            Next();
            var ns = (XmlNameSpaces)ParseXmlNamespaces();
            cx.nsps += (ns.nsps, false);
            for (var s = ns.nsps.First(); s != null; s = s.Next())
                cx.Add(new Namespace(s.key(), s.value(), cx.db.nextPos));
            return (Executable)cx.Add(ns);
        }
        /// <summary>
        /// Parse the Create ordering syntax:
        /// FOR Domain_id (EQUALS|ORDER FULL) BY (STATE|(MAP|RELATIVE) WITH Func_id)  
        /// </summary>
        /// <returns>the executable</returns>
        private Executable? ParseCreateOrdering()
        {
            Next();
            Mustbe(Sqlx.FOR);
            var n = new Ident(this);
            Mustbe(Sqlx.ID);
            Domain ut = ((cx.role.dbobjects[n.ident]is long p)?cx.db.objects[p] as Domain:null)
                ?? throw new DBException("42133", n).Mix();
            OrderCategory fl;
            if (Match(Sqlx.EQUALS))
            {
                fl = OrderCategory.Equals;
                Next();
                Mustbe(Sqlx.ONLY);
            }
            else
            {
                fl = OrderCategory.Full;
                Mustbe(Sqlx.ORDER); Mustbe(Sqlx.FULL);
            }
            Mustbe(Sqlx.BY);
            Sqlx smr = Mustbe(Sqlx.STATE, Sqlx.MAP, Sqlx.RELATIVE);
            if (smr == Sqlx.STATE)
            {
                fl |= OrderCategory.State;
                cx.Add(new Ordering(ut, -1L, fl, cx.db.nextPos, cx));
            }
            else
            {
                fl |= ((smr == Sqlx.RELATIVE) ? OrderCategory.Relative : OrderCategory.Map);
                Mustbe(Sqlx.WITH);
                var (fob, nf) = ParseObjectName();
                var func = fob as Procedure ?? throw new DBException("42000");
                if (smr == Sqlx.RELATIVE && func.arity != 2)
                    throw new DBException("42154", nf).Mix();
                cx.Add(new Ordering(ut, func.defpos, fl, cx.db.nextPos, cx));
            }
            return null;
        }
        /// <summary>
        /// Cols =		'('id { ',' id } ')'.
        /// </summary>
        /// <returns>a list of Ident</returns>
        BList<Ident> ParseIDList()
        {
            bool b = (tok == Sqlx.LPAREN);
            if (b)
                Next();
            var r = BList<Ident>.Empty;
            r += ParseIdent();
            while (tok == Sqlx.COMMA)
            {
                Next();
                r+=ParseIdent();
            }
            if (b)
                Mustbe(Sqlx.RPAREN);
            return r;
        }
        /// <summary>
		/// Cols =		'('ColRef { ',' ColRef } ')'.
        /// </summary>
        /// <returns>a list of coldefpos: returns null if input is (SELECT</returns>
		Domain? ParseColsList(DBObject ob)
        {
            var r = BList<DBObject>.Empty;
            bool b = tok == Sqlx.LPAREN;
            if (b)
                Next();
            if (tok == Sqlx.SELECT)
                return null;
            r+=ParseColRef(ob);
            while (tok == Sqlx.COMMA)
            {
                Next();
                r+=ParseColRef(ob);
            }
            if (b)
                Mustbe(Sqlx.RPAREN);
            return new Domain(cx.GetUid(), cx, Sqlx.TABLE, r, r.Length);
        }
        /// <summary>
        /// ColRef = id { '.' id } .
        /// </summary>
        /// <returns>+- seldefpos</returns>
        DBObject ParseColRef(DBObject ta)
        {
            if (tok == Sqlx.PERIOD)
            {
                Next();
                var pn = lxr.val;
                Mustbe(Sqlx.ID);
                var tb = ((ta is NodeType et) ? cx._Ob(et.structure):ta) as Table
                    ?? throw new DBException("42162", pn).Mix();
                if (cx.db.objects[tb.applicationPS] is not PeriodDef pd || pd.NameFor(cx) != pn.ToString())
                    throw new DBException("42162", pn).Mix();
                return (PeriodDef)cx.Add(pd);
            }
            // We will raise an exception if the column does not exist
            var id = new Ident(this);
            var od = cx._Dom(ta)??Domain.Content;
            var p = od.ColFor(cx, id.ident);
            var tc = cx.obs[p]??cx.db.objects[p] as DBObject??
                throw new DBException("42112", id.ident).Mix();
            Mustbe(Sqlx.ID);
            // We will construct paths as required for any later components
            while (tok == Sqlx.DOT)
            {
                Next();
                var pa = new Ident(this);
                Mustbe(Sqlx.ID);
                if (tc is TableColumn c)
                    tc = new ColumnPath(pa.iix.dp,pa.ident,c,cx.db); // returns a (child)TableColumn for non-documents
                long dm = -1;
                if (tok == Sqlx.AS)
                {
                    Next();
                    tc = (TableColumn)tc.New(cx,tc.mem+(DBObject._Domain, ParseSqlDataType().defpos));
                }
                if (cx.db.objects[od.ColFor(cx,pa.ident)] is TableColumn cc
                    && cx.db is Transaction tr) // create a new path 
                    cx.Add(new PColumnPath(cc.defpos, pa.ToString(), dm, tr.nextPos, cx));
            }
            return cx.Add(tc);
        }
        /// <summary>
        /// id [UNDER id] AS Representation [ Method {',' Method} ] Metadata
		/// Representation = | StandardType 
        ///             | (' Member {',' Member }')' .
        /// </summary>
        /// <returns>the executable</returns>
        Executable? ParseTypeClause()
        {
            var st = cx.GetPos(); // for PTable
            var typename = new Ident(lxr.val.ToString(), cx.Ix(lxr.start,st+1));
            var dt = Domain.TypeSpec;
            var op = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            Ident? undername = null;
            UDType? under = null;
            if (cx.tr == null)
                throw new DBException("2F003");
            Mustbe(Sqlx.ID);
            if (Match(Sqlx.UNDER))
            {
                Next();
                undername = new Ident(this);
                Mustbe(Sqlx.ID,Sqlx.NUMERIC,Sqlx.INTEGER,Sqlx.CHAR,Sqlx.REAL);
            }
            if (undername != null)
            {
                var udm = cx._Dom(cx.GetObject(undername.ident)) ??
                    StandardDataType.Get(dt.kind) ??
                    throw cx.db.Exception("42119", undername).Pyrrho();
                if (udm is UDType)
                    under = (UDType)udm;
                else
                    under = (UDType)(dt.New(-1L,udm.mem));
            }
            if (tok == Sqlx.AS)
            {
                Next();
                if (tok == Sqlx.LPAREN)
                    dt = (UDType)ParseRowTypeSpec(dt.kind, typename, under);
                else
                {
                    var d = ParseStandardDataType() ??
                        throw new DBException("42161", "StandardType", lxr.val.ToString()).Mix();
                    dt = new UDType(d.defpos,d.mem);
                }
            }
            if (Match(Sqlx.RDFLITERAL))
            {
                RdfLiteral rit = (RdfLiteral)lxr.val;
                dt = (UDType)(dt.New(LexPos().dp, dt.mem + (Domain.Iri, rit.dataType?.iri ?? throw new PEException("PE1980"))));
                Next();
            }
            cx.Add(dt);
            var tp = cx.db.nextPos;
            UDType ut;
            if (under is EdgeType)
                ut = BuildEdgeType(tp, typename, BTree<string, SqlValue>.Empty, under.structure);
            else if (under is NodeType)
                ut = BuildNodeType(tp, typename, BTree<string, SqlValue>.Empty, under.structure);
            else
            {
                PType pt = new (typename, dt, under, tp, cx);
                cx.Add(pt);
                ut = (UDType)(cx.obs[tp] ?? throw new PEException("PE1560"));
            }
            //        while (Match(Sqlx.CHECK, Sqlx.CONSTRAINT))
            //            ParseCheckConstraint(pt);
            MethodModes();
            if (Match(Sqlx.OVERRIDING, Sqlx.INSTANCE, Sqlx.STATIC, Sqlx.CONSTRUCTOR, Sqlx.METHOD))
            {
                cx.obs = ObTree.Empty;
                ut = ParseMethodHeader(ut) ?? throw new PEException("PE42140");
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    cx.obs = ObTree.Empty;
                    ut = ParseMethodHeader(ut) ?? throw new PEException("PE42141");
                }
            } 
            cx.parse = op;
            if (StartMetadata(Sqlx.TYPE))
            {
                var ls = BTree<string,SqlValue>.Empty;
                for (var b = ut.rowType.First(); b != null; b = b.Next())
                    if (b.value() is long p)
                    {
                        var tc = cx.obs[p] as TableColumn ?? throw new PEException("PE92731");
                        var dm = cx.obs[tc.domain] as Domain ?? throw new PEException("PE92732");
                        ls += (dm.name,new SqlLiteral(p, dm.name, dm.defaultValue, dm));
                    }
                var m = ParseMetadata(Sqlx.TYPE);
                if (m.Contains(Sqlx.NODETYPE) && ut is not NodeType)
                {
                    ls += ("ID", (SqlValue)cx.Add(new SqlLiteral(cx.GetUid(), TNull.Value, Domain.Char)));
                    BuildNodeType(tp, typename, ls, dt.structure);
                    RemovePType(typename.ident);
                }
                else if (m.Contains(Sqlx.EDGETYPE) && ut is not NodeType) // or EdgeType
                {
                    ls += ("ID", (SqlValue)cx.Add(new SqlLiteral(cx.GetUid(), TNull.Value, Domain.Char)));
                    ls += ("LEAVING", (SqlValue)cx.Add(new SqlLiteral(cx.GetUid(), TNull.Value, Domain.Char)));
                    ls += ("ARRIVING", (SqlValue)cx.Add(new SqlLiteral(cx.GetUid(), TNull.Value, Domain.Char)));
                    var ln = (TChar)(m[Sqlx.ARROWBASE] ?? throw new PEException("PE92410"));
                    var an = (TChar)(m[Sqlx.ARROW] ?? throw new PEException("PE92411"));
                    cx.Add(BuildEdgeType(tp, typename, ls, dt.structure)+(EdgeType.LeavingType,ln)+(EdgeType.ArrivingType,an));
                    RemovePType(typename.ident);
                }
                else if (m != CTree<Sqlx, TypedValue>.Empty)
                    cx.Add(new PMetadata(typename.ident, -1, ut, m, cx.db.nextPos)); 
            }
            return null;
        }
        PTable? FindPTable(string n)
        {
            if (cx.db is Transaction tr)
                for (var b = tr.physicals.First(); b != null; b = b.Next())
                    if (b.value() is PTable pt && pt.name == n)
                        return pt;
            return null;
        }
        void RemovePType(string n)
        {
            if (cx.db is Transaction tr)
                for (var b = tr.physicals.First(); b != null; b = b.Next())
                    if (b.value() is PType pt && (pt.type == Physical.Type.PType || pt.type == Physical.Type.PType1)
                        && pt.name==n)
                    {
                        cx.db += (Transaction.Physicals, tr.physicals - b.key());
                        break;
                    }
        }
        /// <summary>
        /// We have a new node to construct, and need to create a nodetype that is also new.
        /// However, there may be an existing PTable with the same name: in that case we are
        /// merely adding some more columns to it (but changing the order).
        /// We will construct Physicals for PTable (with its domain in its framing),
        /// We will re-use any columns that match st.
        /// and add any properties with names not in st (defpos's between the new PTable and the new PType)
        /// If ID is not provided we will invent one.
        /// </summary>
        /// <param name="pp">The new structure position: we promise a new PTable for this</param>
        /// <param name="typename">The new node type name</param>
        /// <param name="ls">The properties from an inline document, or default values</param>
        /// <param name="st">The structure of the supertype (or -1L)</param>
        /// <returns>The new node type: we promise a new PNodeType for this</returns>
        /// <exception cref="DBException"></exception>
        NodeType BuildNodeType(long pp, Ident typename, BTree<string, SqlValue> ls, long up)
        {
            var un = BTree<string, long?>.Empty; // existing properties from supertype
            Domain? ud = null;
            NodeType? ut = null;
            if (cx._Ob(up) is Table us)
            {
                cx.Add(us.framing);
                ud = cx._Dom(us);
                ut = cx._Dom(us.nodeType) as NodeType;
                for (var b = ud?.rowType.First(); b != null; b = b.Next())
                    if (b.value() is long p && cx._Ob(p) is TableColumn c && c.infos[c.definer] is ObInfo i
                        && i.name is string s)
                        un += (s, p);
            }
            var nst = cx.db.nextStmt;
            var tn = new Ident(typename.ident + ":", typename.iix);
            Table st;
            var pt = FindPTable(tn.ident);
            if (pt == null)
            {
                pt = new PTable(tn.ident, (Domain)Domain.TableType.Relocate(nst), nst, cx.db.nextPos, cx);
                up = -1L;
                st = (Table)((cx.Add(pt) ?? throw new DBException("42105"))
                    ?? throw new DBException("42000"));
            }
            else
                st = (Table)(cx.db.objects[pt.defpos]?? throw new DBException("42105"));
             cx.Add(st);
            var rt = BList<long?>.Empty;
            var rs = CTree<long, Domain>.Empty;
            var sr = BList<long?>.Empty;
            var ss = CTree<long, Domain>.Empty;
            var sn = BTree<string, long?>.Empty; // properties we are adding
            // we need to distinguish between the new nodetype which contains all columns (rt,rs)
            // and the new structure which only holds new columns (sr,ss).
            // sd will be added to the new table's framing.
            var nc = (int)ls.Count;
            var ip = un["ID"]??-1L;
            if (ip<0)
            {
                var pc = new PColumn3(st, "ID", 0, Domain.Char,
                    "", TNull.Value, "", CTree<UpdateAssignment, bool>.Empty,
                    true, GenerationRule.None, cx.db.nextStmt, cx.db.nextPos, cx);
                st = (Table)(cx.Add(pc) ?? throw new DBException("42105"));
                ip = pc.ppos;
                sn += ("ID", ip);
            }
            cx.defs += (new Ident("ID", new Iix(ip)),cx.sD);
            rt += ip; // new node type places ID first
            rs += (ip, ud?.representation[ip]??Domain.Char);
            var tc = (TableColumn)(cx._Ob(ip) ?? throw new DBException("42105"));
            var px = new PIndex(tn.ident, st, new Domain(-1L, cx, Sqlx.ROW, new BList<DBObject>(tc), 1), PIndex.ConstraintType.PrimaryKey,
                -1L, cx.db.nextPos);
            st = (Table)(cx.Add(px) ?? throw new DBException("42105"));
            for (var b = ud?.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.db.objects[p] is TableColumn uc && uc.infos[uc.definer] is ObInfo ci
                        && ci.name is string sc && p != ip)
                {
                    rt += p;
                    rs += (p, ud?.representation[p] ?? Domain.Char);
                    cx.defs += (new Ident(sc, new Iix(p)), cx.sD);
                }
            for (var b = ls?.First(); b != null; b = b.Next())
                if (b.key() is string n && n!="" && !cx.defs.Contains(n)) 
                {
                    var d = cx._Dom(b.value()) ?? Domain.Content;
                    var pc = new PColumn3(st, n, -1, d, "", TNull.Value, "", CTree<UpdateAssignment, bool>.Empty,
                    true, GenerationRule.None, cx.db.nextStmt, cx.db.nextPos, cx);
                    st = (Table)(cx.Add(pc) ?? throw new DBException("42105"));
                    rt += pc.ppos;
                    rs += (pc.ppos, d);
                    cx.defs += (new Ident(n, new Iix(pc.ppos)),cx.sD);
                    sr += pc.ppos;
                    ss += (pc.ppos, d);
                    sn += (n, pc.ppos);
                }
            var sd = (Domain)cx.Add(new Domain(nst, cx, Sqlx.TABLE, rs, rt, rt.Length));
            var fr = Framing.Empty + (Framing.Obs, ObTree.Empty + (sd.defpos, sd));
            st = st + (DBObject._Framing, fr) + (Table._NodeType, cx.db.nextPos) + (DBObject._Domain,nst);
            cx.Add(st);
            var nt = (NodeType)Domain.NodeType.Relocate(cx.db.nextPos) + (Domain.RowType, rt)
                + (Domain.Representation, rs) + (Domain.Structure, st.defpos);
            if (ut != null)
                nt += (UDType.Under, ut);
            cx.Add(nt);
            if (up < 0) // suppress UNDER in this case
                ud = null;
            nt = (NodeType)(cx.Add(new PNodeType(typename,nt, ut, cx.db.nextPos, cx)) ?? throw new DBException("42105"));
            nt += (DBObject.Infos, new BTree<long,ObInfo>(cx.role.defpos,new ObInfo(tn.ident, Grant.Privilege.Usage)));
            var dl = cx.db.loadpos;
            var ro = cx.role + (Role.DBObjects, cx.role.dbobjects + (tn.ident + ":", st.defpos) + (tn.ident, nt.defpos));
            cx.db = cx.db + (nt, dl) + (st, dl) + (ro, dl);
            cx.Add(st.framing);
            return nt;
        }
        /// <summary>
        /// We have a new edge to construct, and need an adgetype that is also new.
        /// We will construct Physicals for PTable (with its domain in its framing),
        /// We will re-use any columns that match st
        /// and add any properties with names not in st (defpos's between the new PTable and the new PType)
        /// If ID is not provided we will invent one.
        /// </summary>
        /// <param name="pp">The new structure position: we promise a new PTable for this</param>
        /// <param name="typename">The new edge type name</param>
        /// <param name="ls">The properties from an inline document, or default values</param>
        /// <param name="st">The structure of the supertype (or -1L)</param>
        /// <returns>The new edge type: we promise a new PEdgeType for this</returns>
        /// <exception cref="DBException"></exception>
        EdgeType BuildEdgeType(long pp,Ident typename,BTree<string,SqlValue> ls,long up)
        {
            var un = BTree<string, long?>.Empty; // existing properties from supertype
            Domain? ud = null;
            if (cx._Ob(up) is Table us)
            {
                cx.Add(us.framing);
                ud = cx._Dom(us);
                for (var b = ud?.rowType.First(); b != null; b = b.Next())
                    if (b.value() is long p && cx._Ob(p) is TableColumn c && c.infos[c.definer] is ObInfo i
                        && i.name is string s)
                        un += (s, p);
            }
            var nst = cx.db.nextStmt;
            var tn = new Ident(typename.ident + ":", typename.iix);
            var pt = new PTable(tn.ident, (Domain)Domain.TableType.Relocate(nst), nst, cx.db.nextPos, cx);
            var st = (Table)((cx.Add(pt) ?? throw new DBException("42105"))
                ?? throw new DBException("42000"));
            cx.Add(st);
            var rt = BList<long?>.Empty;
            var rs = CTree<long, Domain>.Empty;
            var sr = BList<long?>.Empty;
            var ss = CTree<long, Domain>.Empty;
            var sn = BTree<string, long?>.Empty; // properties we are adding
            // we need to distinguish between the new nodetype which contains all columns (rt,rs)
            // and the new structure which only holds new columns (sr,ss).
            // sd will be added to the new table's framing.
            var nc = (int)ls.Count;
            var ip = un["ID"] ?? -1L;
            if (ip < 0)
            {
                var pc = new PColumn3(st, "ID", 0, Domain.Char,
                    "", TNull.Value, "", CTree<UpdateAssignment, bool>.Empty,
                    true, GenerationRule.None, cx.db.nextStmt, cx.db.nextPos, cx);
                st = (Table)(cx.Add(pc) ?? throw new DBException("42105"));
                ip = pc.ppos;
                sn += ("ID", ip);
                cx.defs += (new Ident("ID", new Iix(ip)), cx.sD);
            }
            rt += ip; // new node type places ID first
            rs += (ip, ud?.representation[ip] ?? Domain.Char);
            var tc = (TableColumn)(cx._Ob(ip) ?? throw new DBException("42105"));
            var px = new PIndex(tn.ident, st, new Domain(-1L, cx, Sqlx.ROW, new BList<DBObject>(tc), 1), PIndex.ConstraintType.PrimaryKey,
                -1L, cx.db.nextPos);
            st = (Table)(cx.Add(px) ?? throw new DBException("42105"));
            var lp = un["LEAVING"] ?? -1L;
            if (lp < 0)
            {
                var pc = new PColumn3(st, "LEAVING", 1, Domain.Char,
                    "", TNull.Value, "", CTree<UpdateAssignment, bool>.Empty,
                    true, GenerationRule.None, cx.db.nextStmt, cx.db.nextPos, cx);
                st = (Table)(cx.Add(pc) ?? throw new DBException("42105"));
                lp = pc.ppos;
                sn += ("LEAVING", lp);
                cx.defs += (new Ident("LEAVING", new Iix(lp)), cx.sD);
            }
            rt += lp; // new edge type places LEAVING next
            rs += (lp, ud?.representation[lp] ?? Domain.Char);
            tc = (TableColumn)(cx._Ob(lp) ?? throw new DBException("42105"));
            px = new PIndex(tn.ident, st, new Domain(-1L, cx, Sqlx.ROW, new BList<DBObject>(tc), 1), 
                PIndex.ConstraintType.ForeignKey|PIndex.ConstraintType.CascadeUpdate,
                -1L, cx.db.nextPos);
            st = (Table)(cx.Add(px) ?? throw new DBException("42105")); 
            var ap = un["ARRIVING"] ?? -1L;
            if (ap < 0)
            {
                var pc = new PColumn3(st, "ARRIVING", 2, Domain.Char,
                    "", TNull.Value, "", CTree<UpdateAssignment, bool>.Empty,
                    true, GenerationRule.None, cx.db.nextStmt, cx.db.nextPos, cx);
                st = (Table)(cx.Add(pc) ?? throw new DBException("42105"));
                ap = pc.ppos;
                sn += ("ARRIVING", ap);
                cx.defs += (new Ident("ARRIVING", new Iix(lp)), cx.sD);
            }
            rt += ap; // new edge type places ARRIVING next
            rs += (ap, ud?.representation[ap] ?? Domain.Char);
            tc = (TableColumn)(cx._Ob(ap) ?? throw new DBException("42105"));
            px = new PIndex(tn.ident, st, new Domain(-1L, cx, Sqlx.ROW, new BList<DBObject>(tc), 1),
                PIndex.ConstraintType.ForeignKey | PIndex.ConstraintType.CascadeUpdate,
                -1L, cx.db.nextPos);
            st = (Table)(cx.Add(px) ?? throw new DBException("42105"));
            for (var b = ud?.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.db.objects[p] is TableColumn uc 
                    && uc.infos[uc.definer] is ObInfo ci && ci.name is string sc
                        && p != ip && p != lp && p != ap) // we already have ID, LEAVING, ARRIVING
                {
                    rt += p;
                    rs += (p, ud?.representation[p] ?? Domain.Char);
                    cx.defs += (new Ident(sc, new Iix(p)), cx.sD);
                }

            for (var b = ls.First(); b != null; b = b.Next())
                if (b.key() is string n && n != "" && !cx.defs.Contains(n))
                {
                    var d = cx._Dom(b.value()) ?? Domain.Content;
                    var pc = new PColumn3(st, n, -1, d, "", TNull.Value, "", CTree<UpdateAssignment, bool>.Empty,
                    true, GenerationRule.None, cx.db.nextStmt, cx.db.nextPos, cx);
                    st = (Table)(cx.Add(pc) ?? throw new DBException("42105"));
                    sn += (pc.name, pc.ppos);
                    rt += pc.ppos;
                    rs += (pc.ppos, d);
                    sr += pc.ppos;
                    ss += (pc.ppos, d);
                }
            var sd = (Domain)cx.Add(new Domain(nst, cx, Sqlx.TABLE, rs, rt, rt.Length));
            st = (Table)cx.Add(st + (DBObject._Framing, new Framing(cx, nst)) + (Table._NodeType, cx.db.nextPos));
            var et = (EdgeType)Domain.EdgeType.Relocate(cx.db.nextPos) + (Domain.RowType, rt)
                + (Domain.Representation, rs) + (Domain.Structure, st.defpos);
            if (ls["LEAVING"] is SqlLiteral sl && cx.db.nodeIds[sl.val.ToString()] is TNode ln)
                et += (EdgeType.LeavingType, ln.dataType.defpos);
            if (ls["ARRIVING"] is SqlLiteral sa && cx.db.nodeIds[sa.val.ToString()] is TNode an)
                et += (EdgeType.ArrivingType, an.dataType.defpos);
            var al = ls["ARRIVING"]?.id;
            cx.Add(et);
            et = (EdgeType)(cx.Add(new PEdgeType(typename, et, ud, cx.db.nextPos, cx)) ?? throw new DBException("42105"));
            et += (DBObject.Infos, new BTree<long,ObInfo>(cx.role.defpos,new ObInfo(tn.ident, Grant.Privilege.Usage)));
            var dl = cx.db.loadpos;
            var ro = cx.role + (Role.DBObjects, cx.role.dbobjects + (tn.ident + ":", st.defpos) + (tn.ident, et.defpos));
            cx.db = cx.db + (et, dl) + (st, dl) + (ro,dl);
            cx.Add(st.framing);
            if (NodeTypeFor(ls["LEAVING"]?.ToString() ?? "") is long pl && cx._Ob(pl) is NodeType lt
                 && NodeTypeFor(ls["ARRIVING"]?.ToString() ?? "") is long pa && cx._Ob(pa) is NodeType at)
                et = et + (EdgeType.LeavingType, LeastSpecific(lt)) + (EdgeType.ArrivingType, LeastSpecific(at));
            et = (EdgeType)cx.Add(et);
            return et;
        }
        NodeType CheckNodeType(NodeType nt,CTree<string,SqlValue>ls)
        {
            if (cx._Ob(nt.structure) is not Table tb || nt.infos[nt.definer] is not ObInfo ni)
                throw new PEException("PE91721");
            var nc = (int)ni.names.Count;
            var ot = tb;
            for (var b = ls?.First(); b != null; b = b.Next())
                if (b.value() is SqlValue sv && sv.name is string n && !ni.names.Contains(n))
                {
                    var d = cx._Dom(b.value()) ?? Domain.Content;
                    var pc = new PColumn3(tb, n, nc++, d, "", TNull.Value, "", CTree<UpdateAssignment, bool>.Empty,
                    true, GenerationRule.None, cx.db.nextStmt, cx.db.nextPos, cx);
                    tb = (Table)(cx.Add(pc) ?? throw new DBException("42105"));
                }
            if (tb == ot)
                return nt;
            var dm = cx.Add(new Edit(nt, nt.name, cx._Dom(tb)??Domain.Content, 
                cx.db.nextPos, cx)) ?? throw new DBException("42105");
            // convert to a NodeType
            return (NodeType)cx.Add(new NodeType(dm.defpos, dm.mem));
        }
        EdgeType CheckEdgeType(EdgeType et, CTree<string,SqlValue> ls)
        {
            var nt = CheckNodeType(et, ls);
            if (nt != et) // convert to an EdgeType
                et = (EdgeType)cx.Add(new EdgeType(nt.defpos, nt.mem));
            if (cx._Ob(et.structure) is not Table)
                throw new PEException("PE91722");
            if (NodeTypeFor(ls["LEAVING"]?.ToString()??"") is long lp && cx._Ob(lp) is NodeType lt
                && cx._Dom(cx._Ob(et.leavingType)) is Domain el
                && !lt.EqualOrStrongSubtypeOf(el))
            {
                var pp = cx.db.nextPos;
                var pi = new Ident(pp.ToString() + ":", new Iix(pp));
                var xt = (Domain)cx.Add(et + (EdgeType.LeavingType, 
                    BuildNodeType(pp, pi, CTree<string,SqlValue>.Empty,el.structure).defpos));
                cx.Add(xt);
     //           cx.Add(new EditType(new Ident(lt.name,new Iix(lt.defpos)),lt,el,xt,cx.db.nextPos,cx));
            }
            if (NodeTypeFor(ls["ARRIVING"]?.ToString()??"") is long ap && cx._Ob(ap) is NodeType at 
                && cx._Dom(cx._Ob(et.arrivingType)) is Domain ea
                && !at.EqualOrStrongSubtypeOf(ea))
            {
                var pp = cx.db.nextPos;
                var pi = new Ident(pp.ToString() + ":", new Iix(pp));
                var xt = (Domain)cx.Add(et + (EdgeType.LeavingType, 
                    BuildNodeType(pp, pi, CTree<string,SqlValue>.Empty, ea.structure).defpos));
                cx.Add(xt);
     //           cx.Add(new EditType(new Ident(at.name, new Iix(at.defpos)), at, ea, xt, cx.db.nextPos, cx));
            }
            return et;
        }
        long NodeTypeFor(string s)
        {
            if (cx.db.nodeIds[s] is TNode n)
                    return n.dataType.defpos;
            return -1L;
        }
        long LeastSpecific(NodeType nt)
        {
            while (nt.super is NodeType st)
                nt = st;
            return nt.defpos;
        }
        /// <summary>
        /// Method =  	MethodType METHOD id '(' Parameters ')' [RETURNS Type] [FOR id].
        /// MethodType = 	[ OVERRIDING | INSTANCE | STATIC | CONSTRUCTOR ] .
        /// </summary>
        /// <returns>the methodname parse class</returns>
        MethodName ParseMethod(Domain? xp=null)
        {
            MethodName mn = new()
            {
                methodType = PMethod.MethodType.Instance
            };
            switch (tok)
            {
                case Sqlx.OVERRIDING: Next(); mn.methodType = PMethod.MethodType.Overriding; break;
                case Sqlx.INSTANCE: Next(); break;
                case Sqlx.STATIC: Next(); mn.methodType = PMethod.MethodType.Static; break;
                case Sqlx.CONSTRUCTOR: Next(); mn.methodType = PMethod.MethodType.Constructor; break;
            }
            Mustbe(Sqlx.METHOD);
            mn.name = new Ident(lxr.val.ToString(), cx.Ix(cx.db.nextPos));
            Mustbe(Sqlx.ID);
            int st = lxr.start;
            if (mn.name is not Ident nm)
                throw new DBException("42000");
            mn.ins = ParseParameters(mn.name,xp);
            mn.mname = new Ident(nm.ident, nm.iix);
            for (var b = mn.ins.First(); b != null; b = b.Next())
                if (b.value() is long p)
                {
                    var pa = (FormalParameter?)cx.obs[p] ?? throw new PEException("PE1621");
                    cx.defs += (new Ident(mn.mname, new Ident(pa.name ?? "", new Iix(pa.defpos))), 
                        mn.mname.iix);
                }
            mn.retType = ParseReturnsClause(mn.mname);
            mn.signature = new string(lxr.input, st, lxr.start - st);
            if (tok == Sqlx.FOR)
            {
                Next();
                var tname = new Ident(this);
                var ttok = tok;
                Mustbe(Sqlx.ID,Sqlx.NUMERIC,Sqlx.CHAR,Sqlx.INTEGER,Sqlx.REAL);
                xp = (Domain?)cx.db.objects[cx.role.dbobjects[tname.ident]??-1L]??
                    StandardDataType.Get(ttok);
            } else if (mn.methodType==PMethod.MethodType.Constructor) 
                xp = (Domain?)cx.db.objects[cx.role.dbobjects[mn.name.ident]??-1L];
            mn.type = xp as UDType ?? throw new DBException("42000", "UDType").ISO();
            return mn;
        }
        /// <summary>
        /// Define a new method header (calls ParseMethod)
        /// </summary>
        /// <param name="xp">the UDTtype if we are creating a Type</param>
        /// <returns>the PMethod</returns>
		UDType? ParseMethodHeader(Domain? xp=null)
        {
            MethodName mn = ParseMethod(xp);
            if (mn.name is not Ident nm || mn.retType==null || mn.type==null)
                throw new DBException("42000");
            var r = new PMethod(nm.ident, mn.ins,
                mn.retType, mn.methodType, mn.type, null,
                new Ident(mn.signature, nm.iix), cx.db.nextStmt, cx.db.nextPos, cx);
            cx.Add(r);
            return (xp!=null)?(UDType?)cx._Ob(xp.defpos):null;
        }
        /// <summary>
        /// Create a method body (called when parsing CREATE METHOD or ALTER METHOD)
        /// </summary>
        /// <returns>the executable</returns>
		Executable? ParseMethodDefinition()
        {
            var op = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            MethodName mn = ParseMethod(); // don't want to create new things for header
            if (mn.name is not Ident nm || mn.retType == null || mn.type == null)
                throw new DBException("42000");
            var ut = mn.type;
            var oi = ut.infos[cx.role.defpos];
            var meth = cx.db.objects[oi?.methodInfos[nm.ident]?[cx.Signature(mn.ins)] ?? -1L] as Method ??
    throw new DBException("42132", nm.ToString(), oi?.name ?? "??").Mix();
            var lp = LexPos();
            int st = lxr.start;
            var nst = cx.db.nextStmt;
            cx.obs = ObTree.Empty;
            cx.Add(meth.framing); // for formals from meth
                                  //            var nst = meth.framing.obs.First()?.key()??cx.db.nextStmt;
            cx.defs = Ident.Idents.Empty;
            for (var b = meth.ins.First(); b != null; b = b.Next())
                if (b.value() is long p)
                {
                    var pa = (FormalParameter?)cx.obs[p] ?? throw new DBException("42000");
                    var px = cx.Ix(pa.defpos);
                    cx.defs += (new Ident(pa.name ?? "", Iix.None), px);
                }
            ut.Instance(lp.dp, cx);
            meth += (Procedure.Body, ParseProcedureStatement(mn.retType)?.defpos ?? throw new DBException("42000"));
            Ident ss = new(new string(lxr.input, st, lxr.start - st), lp);
            cx.parse = op;
            // we really should check the signature here
            var md = new Modify(meth.defpos, meth, ss, nst, cx.db.nextPos, cx);
            cx.Add(md);
            cx.result = -1L;
            return null;
        }
        /// <summary>
        /// DomainDefinition = id [AS] StandardType [DEFAULT TypedValue] { CheckConstraint } Collate.
        /// </summary>
        /// <returns>the executable</returns>
        Executable? ParseDomainDefinition()
        {
            var colname = new Ident(this);
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.AS)
                Next();
            var type = ParseSqlDataType();
            if (tok == Sqlx.DEFAULT)
            {
                Next();
                int st = lxr.start;
                var dv = ParseSqlValue(type);
                Next();
                var ds = new string(lxr.input, st, lxr.start - st);
                type = (Domain)type.New(cx,type.mem + (Domain.Default, dv) + (Domain.DefaultString, ds));
            }
            PDomain pd;
            if (type.iri != null)
                pd = new PDomain1(colname.ident, type, cx.db.nextPos, cx);
            else
                pd = new PDomain(colname.ident, type, cx.db.nextPos, cx);
            cx.Add(pd);
            var a = new List<Physical>();
            while (Match(Sqlx.NOT, Sqlx.CONSTRAINT, Sqlx.CHECK))
                if (ParseCheckConstraint(pd) is PCheck ck)
                    a.Add(ck);
            if (tok == Sqlx.COLLATE)
                pd.domain += (Domain.Culture, new CultureInfo(ParseCollate()));
            return null;
        }
        /// <summary>
        /// Parse a collation indication
        /// </summary>
        /// <returns>The collation name</returns>
        string ParseCollate()
        {
            Next();
            var collate = lxr.val;
            Mustbe(Sqlx.ID);
            return collate.ToString();
        }
        /// <summary>
        /// CheckConstraint = [ CONSTRAINT id ] CHECK '(' [XMLOption] SqlValue ')' .
        /// </summary>
        /// <param name="pd">the domain</param>
        /// <returns>the PCheck object resulting from the parse</returns>
        PCheck? ParseCheckConstraint(PDomain pd)
        {
            var oc = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            var o = new Ident(this);
            Ident n;
            if (tok == Sqlx.CONSTRAINT)
            {
                Next();
                n = o;
                Mustbe(Sqlx.ID);
            }
            else
                n = new Ident(this);
            Mustbe(Sqlx.CHECK);
            Mustbe(Sqlx.LPAREN);
            var nst = cx.db.nextStmt;
            var st = lxr.start;
            var se = ParseSqlValue(Domain.Bool).Reify(cx);
            Mustbe(Sqlx.RPAREN);
            PCheck? pc = null;
            if (cx.db.objects[pd.defpos] is DBObject ob)
            {
                pc = new PCheck(ob, n.ident, se,
                    new string(lxr.input, st, lxr.start - st), nst, cx.db.nextPos, cx);
                cx.Add(pc);
            }
            cx.parse = oc;
            return pc;
        }
        /// <summary>
        /// id TableContents [UriType] [Clasasification] [Enforcement] {Metadata} 
        /// </summary>
        /// <returns>the executable</returns>
        void ParseCreateTable()
        {
            var op = cx.parse;
            var name = new Ident(this);
            cx.parse = ExecuteStatus.Compile;
            Mustbe(Sqlx.ID);
            if (cx.db.schema.dbobjects.Contains(name.ident) || cx.role.dbobjects.Contains(name.ident))
                throw new DBException("42104", name);
            var pt = new PTable(name.ident, new Domain(cx.GetUid(), cx, Sqlx.TABLE, BList<DBObject>.Empty), 
                cx.db.nextStmt, cx.db.nextPos, cx);
            var tb = (Table)(cx.Add(pt)??throw new DBException("42105"));
            tb = ParseTableContentsSource(tb);
            if (tok == Sqlx.RDFLITERAL && 
                lxr.val is RdfLiteral rit && rit.dataType.iri is string ri)
            {
                tb += (Domain.Iri, ri);
                tb = (Table)cx.Add(tb);
                Next();
            }
            cx.parse = op;
            if (Match(Sqlx.SECURITY))
            {
                Next();
                if (Match(Sqlx.LEVEL))
                    tb = (Table)ParseClassification(tb);
                if (tok == Sqlx.SCOPE)
                    tb = ParseEnforcement(tb);
            }
            if (StartMetadata(Sqlx.TABLE))
            {
                var dp = LexDp();
                var md = ParseMetadata(Sqlx.TABLE);
                cx.Add(new PMetadata(name.ident,dp,tb,md,cx.db.nextPos));
            }
        }
        DBObject ParseClassification(DBObject ob)
        {
            var lv = MustBeLevel();
            ob = cx.Add(new Classify(ob.defpos, lv, cx.db.nextPos))
                ?? throw new DBException("42105");
            return ob;
        }
        /// <summary>
        /// Enforcement = SCOPE [READ] [INSERT] [UPDATE] [DELETE]
        /// </summary>
        /// <returns></returns>
        Table ParseEnforcement(Table tb)
        {
            if (cx.db == null || cx.db.user == null || cx.db.user.defpos != cx.db.owner)
                throw new DBException("42105");
            Mustbe(Sqlx.SCOPE);
            Grant.Privilege r = Grant.Privilege.NoPrivilege;
            while (Match(Sqlx.READ, Sqlx.INSERT, Sqlx.UPDATE, Sqlx.DELETE))
            {
                switch (tok)
                {
                    case Sqlx.READ: r |= Grant.Privilege.Select; break;
                    case Sqlx.INSERT: r |= Grant.Privilege.Insert; break;
                    case Sqlx.UPDATE: r |= Grant.Privilege.Update; break;
                    case Sqlx.DELETE: r |= Grant.Privilege.Delete; break;
                }
                Next();
            }
            tb = (Table)(cx.Add(new Enforcement(tb, r, cx.db.nextPos)) ?? throw new DBException("42105"));
            return tb;
        }
        /// <summary>
        /// TebleContents = '(' TableClause {',' TableClause } ')' { VersioningClause }
        /// | OF Type_id ['(' TypedTableElement { ',' TypedTableElement } ')'] .
        /// VersioningClause = WITH (SYSTEM|APPLICATION) VERSIONING .
        /// </summary>
        /// <param name="ta">The newly defined Table</param>
        /// <returns>The iri or null</returns>
        Table ParseTableContentsSource(Table tb)
        {
            switch (tok)
            {
                case Sqlx.LPAREN:
                    Next();
                    tb = ParseTableItem(tb);
                    while (tok == Sqlx.COMMA)
                    {
                        Next();
                        tb = ParseTableItem(tb);
                    }
                    Mustbe(Sqlx.RPAREN);
                    while (Match(Sqlx.WITH))
                        tb = ParseVersioningClause(tb, false);
                    break;
                case Sqlx.OF:
                    {
                        Next();
                        var id = ParseIdent();
                        var udt = cx.db.objects[cx.role.dbobjects[id.ident]??-1L] as Domain??
                            throw new DBException("42133", id.ToString()).Mix();
                        var tr = cx.db as Transaction?? throw new DBException("2F003");
                        for (var cd = udt.rowType.First(); cd != null; cd = cd.Next())
                            if (cd.value() is long p && cx.db.objects[p] is TableColumn tc &&
                                cx._Dom(tc) is Domain cc &&
                                tc.infos[cx.role.defpos] is ObInfo ci && ci.name!=null)
                                tb = (Table)(cx.Add(new PColumn2(tb, ci.name, cd.key(), cc,
                                    tc.generated.gfs ?? cc.defaultValue?.ToString() ?? "",
                                    cc.defaultValue??TNull.Value, tc.notNull,
                                    tc.generated, cx.db.nextStmt, tr.nextPos, cx)) 
                                    ?? throw new DBException("42105"));
                        if (Match(Sqlx.LPAREN))
                        {
                            for (; ; )
                            {
                                Next();
                                if (Match(Sqlx.UNIQUE, Sqlx.PRIMARY, Sqlx.FOREIGN))
                                    tb = ParseTableConstraintDefin(tb);
                                else
                                {
                                    id = ParseIdent();
                                    var se = (TableColumn?)cx.db.objects[udt.ColFor(cx,id.ident)]
                                        ??throw new DBException("42112",id.ident);
                                    ParseColumnOptions(tb, se);
                                    tb = (Table?)cx.obs[tb.defpos] ?? throw new PEException("PE1711");
                                }
                                if (!Match(Sqlx.COMMA))
                                    break;
                            }
                            Mustbe(Sqlx.RPAREN);
                        }
                        break;
                    }
                default: throw new DBException("42161", "(, AS, or OF", tok.ToString()).Mix();
            }
            return tb;
        }
        /// <summary>
        /// Parse the table versioning clause:
        /// (SYSTEM|APPLICATION) VERSIONING
        /// </summary>
        /// <param name="ta">the table</param>
        /// <param name="drop">whether we are dropping an existing versioning</param>
        /// <returns>the updated Table object</returns>
        private Table ParseVersioningClause(Table tb, bool drop)
        {
            Next();
            var sa = tok;
            Mustbe(Sqlx.SYSTEM, Sqlx.APPLICATION);
            var vi = (sa == Sqlx.SYSTEM) ? PIndex.ConstraintType.SystemTimeIndex : PIndex.ConstraintType.ApplicationTimeIndex;
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            var pi = tb.FindPrimaryIndex(cx) ?? throw new DBException("42000");
            if (drop)
            {
                var fl = (vi == 0) ? PIndex.ConstraintType.SystemTimeIndex : PIndex.ConstraintType.ApplicationTimeIndex;
                for (var e = tb.indexes.First(); e != null; e = e.Next())
                    for (var c = e.value().First(); c != null; c = c.Next())
                        if (cx.db.objects[c.key()] is Level3.Index px &&
                                    px.tabledefpos == tb.defpos && (px.flags & fl) == fl)
                            tb= (Table)(cx.Add(new Drop(px.defpos, tr.nextPos))
                                ?? throw new DBException("42105"));
                if (sa == Sqlx.SYSTEM && cx.db.objects[tb.systemPS] is PeriodDef pd)
                    tb = (Table)(cx.Add(new Drop(pd.defpos, tr.nextPos))
                        ?? throw new DBException("42105"));
                Mustbe(Sqlx.VERSIONING);
                return tb;
            }
            var ti = tb.infos[cx.role.defpos];
            if (ti==null || sa == Sqlx.APPLICATION)
                throw new DBException("42164", tb.NameFor(cx)).Mix();
            pi = (Level3.Index)(cx.Add(new PIndex("", tb, pi.keys,
                    PIndex.ConstraintType.PrimaryKey | vi,-1L, tr.nextPos))
                ?? throw new DBException("42105")); 
            Mustbe(Sqlx.VERSIONING);
            var ixs = tb.indexes;
            var iks = ixs[pi.keys] ?? CTree<long, bool>.Empty;
            iks += (pi.defpos, true);
            ixs += (pi.keys, iks);
            return (Table)cx.Add(tb + (Table.Indexes, tb.indexes + ixs));
        }
        /// <summary>
        /// TypedTableElement = id WITH OPTIONS '(' ColumnOption {',' ColumnOption} ')'.
        /// ColumnOption = (SCOPE id)|(DEFAULT TypedValue)|ColumnConstraint .
        /// </summary>
        /// <param name="tb">The table being created</param>
        /// <param name="tc">The column being optioned</param>
        TableColumn ParseColumnOptions(Table tb, TableColumn tc)
        {
            Mustbe(Sqlx.WITH);
            Mustbe(Sqlx.OPTIONS);
            Mustbe(Sqlx.LPAREN);
            for (; ; )
            {
                switch (tok)
                {
                    case Sqlx.SCOPE: Next();
                        Mustbe(Sqlx.ID); // TBD
                        break;
                    case Sqlx.DEFAULT:
                        {
                            Next();
                            int st = lxr.start;
                            var dt = cx._Dom(tc) ?? throw new PEException("PE1535");
                            var dv = ParseSqlValue(dt);
                            var ds = new string(lxr.input, st, lxr.start - st);
                            var dm = cx._Dom(dt.defpos, (Domain.Default, dv), (Domain.DefaultString, ds));
                            cx.Add(dm);
                            tc += (DBObject._Domain, dm.defpos);
                            cx.db += (tc, cx.db.loadpos);
                            break;
                        }
                    default: tc = ParseColumnConstraint(tb, tc);
                        break;
                }
                if (Match(Sqlx.COMMA))
                    Next();
                else
                    break;
            }
            Mustbe(Sqlx.RPAREN);
            return tc;
        }
        /// <summary>
        /// TableClause =	ColumnDefinition | TableConstraintDef | TablePeriodDefinition .
        /// </summary>
        Table ParseTableItem(Table tb)
        {
            if (Match(Sqlx.PERIOD))
                tb = AddTablePeriodDefinition(tb);
            else if (tok == Sqlx.ID)
                tb = ParseColumnDefin(tb);
            else
                tb = ParseTableConstraintDefin(tb);
            return tb;
        }
        /// <summary>
        /// TablePeriodDefinition = PERIOD FOR PeriodName '(' id, id ')' .
        /// PeriodName = SYSTEM_TIME | id .
        /// </summary>
        /// <returns>the TablePeriodDefinition</returns>
        TablePeriodDefinition ParseTablePeriodDefinition()
        {
            var r = new TablePeriodDefinition();
            Next();
            Mustbe(Sqlx.FOR);
            if (Match(Sqlx.SYSTEM_TIME))
                Next();
            else
            {
                r.periodname = new Ident(this);
                Mustbe(Sqlx.ID);
                r.pkind = Sqlx.APPLICATION;
            }
            Mustbe(Sqlx.LPAREN);
            r.col1 = ParseIdent();
            Mustbe(Sqlx.COMMA);
            r.col2 = ParseIdent();
            Mustbe(Sqlx.RPAREN);
            return r;
        }
        /// <summary>
        /// Add columns for table period definition
        /// </summary>
        /// <param name="tb"></param>
        /// <returns>the updated table</returns>
        Table AddTablePeriodDefinition(Table tb)
        {
            var ptd = ParseTablePeriodDefinition();
            var rt = cx._Dom(tb);
            if (rt == null || ptd.col1==null || ptd.col2==null)
                throw new DBException("42105");
            var c1 = (TableColumn?)cx.db.objects[rt.ColFor(cx,ptd.col1.ident)];
            var c2 = (TableColumn?)cx.db.objects[rt.ColFor(cx,ptd.col2.ident)];
            var c1t = cx._Dom(c1);
            var c2t = cx._Dom(c2);
            if (c1 == null||c1t==null)
                throw new DBException("42112", ptd.col1).Mix();
            if (c1t.kind != Sqlx.DATE && c1t.kind != Sqlx.TIMESTAMP)
                throw new DBException("22005R", "DATE or TIMESTAMP", c1t.ToString()).ISO()
                    .AddType(Domain.UnionDate).AddValue(c1t);
            if (c2 == null||c2t==null)
                throw new DBException("42112", ptd.col2).Mix();
            if (c2t.kind != Sqlx.DATE && c2t.kind != Sqlx.TIMESTAMP)
                throw new DBException("22005R", "DATE or TIMESTAMP", c2t.ToString()).ISO()
                    .AddType(Domain.UnionDate).AddValue(c1t);
            if (c1t.CompareTo(c2t)!=0)
                throw new DBException("22005S", c1t.ToString(), c2t.ToString()).ISO()
                    .AddType(c1t).AddValue(c2t);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            return (Table)(cx.Add(new PPeriodDef(tb, ptd.periodname.ident, c1.defpos, c2.defpos,
                tr.nextPos, cx)) ?? throw new DBException("42105"));
        }
        /// <summary>
		/// ColumnDefinition = id Type [DEFAULT TypedValue] {ColumnConstraint|CheckConstraint} Collate {Metadata}
		/// |	id Type GENERATED ALWAYS AS '('Value')'
        /// |   id Type GENERATED ALWAYS AS ROW (START|NEXT|END).
        /// Type = ...|	Type ARRAY
        /// |	Type MULTISET 
        /// </summary>
        /// <param name="tb">the table</param>
        /// <returns>the updated table</returns>
        Table ParseColumnDefin(Table tb)
        {
            var type = Domain.Null;
            var dom = Domain.Null;
            if (Match(Sqlx.COLUMN))
                Next();
            var colname = new Ident(this);
            var lp = colname.iix;
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.ID)
            {
                var op = cx.db.role.dbobjects[new Ident(this).ident];
                type = cx.db.objects[op??-1L] as Domain
                    ?? throw new DBException("42119", lxr.val.ToString());
                Next();
            }
            else if (Match(Sqlx.CHARACTER, Sqlx.CHAR, Sqlx.VARCHAR, Sqlx.NATIONAL, Sqlx.NCHAR,
                Sqlx.BOOLEAN, Sqlx.NUMERIC, Sqlx.DECIMAL,
                Sqlx.DEC, Sqlx.FLOAT, Sqlx.REAL, Sqlx.DOUBLE,
                Sqlx.INT, Sqlx.INTEGER, Sqlx.BIGINT, Sqlx.SMALLINT, Sqlx.PASSWORD,
                Sqlx.BINARY, Sqlx.BLOB, Sqlx.NCLOB, Sqlx.CLOB, Sqlx.XML,
                Sqlx.DATE, Sqlx.TIME, Sqlx.TIMESTAMP, Sqlx.INTERVAL,
                Sqlx.DOCUMENT, Sqlx.DOCARRAY, Sqlx.CHECK,
#if MONGO
                Sqlx.OBJECT, // v5.1
#endif
                Sqlx.ROW, Sqlx.TABLE, Sqlx.REF))
                type = ParseSqlDataType();
            dom = type;
            if (Match(Sqlx.ARRAY))
            {
                dom = (Domain)cx.Add(new Domain(cx.GetUid(),Sqlx.ARRAY, type));
                Next();
            }
            else if (Match(Sqlx.MULTISET))
            {
                dom = (Domain)cx.Add(new Domain(cx.GetUid(),Sqlx.MULTISET, type));
                Next();
            }
            var ua = CTree<UpdateAssignment,bool>.Empty;
            var gr = GenerationRule.None;
            var dfs = "";
            var nst = cx.db.nextStmt;
            TypedValue dv = TNull.Value;
            if (tok == Sqlx.DEFAULT)
            {
                Next();
                int st = lxr.start;
                dv = lxr.val;
                dfs = new string(lxr.input, st, lxr.pos - st);
                Next();
            }
            else if (Match(Sqlx.GENERATED))
            {
                dv = dom.defaultValue ?? TNull.Value;
                // Set up the information for parsing the generation rule
                // The table domain and cx.defs should contain the columns so far defined
                var oc = cx;
                cx = cx.ForConstraintParse();
                gr = ParseGenerationRule(lp.dp,dom);
                dfs = gr.gfs;
                oc.DoneCompileParse(cx);
                cx = oc;
            }
            if (dom == null)
                throw new DBException("42120", colname.ident);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var pc = new PColumn3(tb, colname.ident, (int)tb.tableCols.Count, dom,
                dfs, dv, "", ua, false, gr, cx.db.nextStmt, tr.nextPos, cx);
            cx.Add(pc);
            tb = (Table?)cx.obs[tb.defpos] ?? tb;
            var tc = (TableColumn)(cx.db.objects[pc.defpos]??throw new PEException("PE50100"));
            if (gr.exp >= 0)
                tc = cx.Modify(pc, tc, nst);
            while (Match(Sqlx.NOT, Sqlx.REFERENCES, Sqlx.CHECK, Sqlx.UNIQUE, Sqlx.PRIMARY, Sqlx.CONSTRAINT,
                Sqlx.SECURITY))
            {
                var oc = cx;
                nst = cx.db.nextStmt;
                cx = cx.ForConstraintParse();
                tc = ParseColumnConstraintDefin(tb, pc, tc);
                tb = (Table?)cx.obs[tb.defpos] ?? throw new PEException("PE1570");
                oc.DoneCompileParse(cx);
                cx = oc;
                tc = cx.Modify(pc,tc,nst);
                tb = cx.Modify(tb, nst); // maybe the framing changes since nst are irrelevant??
            }
            if (type != null && tok == Sqlx.COLLATE)
                dom = new Domain(pc.ppos,type.kind,type.mem+(Domain.Culture,new CultureInfo(ParseCollate())));
            return tb;
        }
/*        /// <summary>
        /// Called in 2 places (identified by value of rest)
        /// 1. processing of CREATE VIEW .. OF( .. RestView Definition: rest is empty virtual table def
        /// 2. load of database containing RestView: rest is null 
        /// Cases 1 and 2 need to parse the source, case 1 needs to define columns and VIndexes
        /// </summary>
        /// <param name="vt"></param>
        /// <param name="rest"></param>
        /// <returns>domain and rest</returns>
        internal (Domain,BList<Physical>) ParseRestViewSpec(long vt,BList<Physical> rest = null)
        {
            Next();
            var ns = BList<SqlValue>.Empty;
            (ns,rest) = ParseRestViewItem(vt, ns, rest);
            while (tok == Sqlx.COMMA)
            {
                Next();
                (ns, rest) = ParseRestViewItem(vt, ns, rest);
            }
            var dm = new Domain(cx.GetUid(), cx, Sqlx.TABLE, ns) + (Domain.Structure, vt);
            cx.Add(dm);
            Mustbe(Sqlx.RPAREN);
            return (dm,rest);
        }
        /// <summary>
        /// Called in 2 places (identified by value of rest)
        /// 1. processing of CREATE VIEW .. OF( .. RestView Definition: rest is empty virtual table def
        /// 2. load of database containing RestView: rest is null 
        /// Cases 1 and 2 need to parse the source, case 1 needs to define columns and VIndexes
        /// </summary>
        /// <param name="t"></param>
        /// <param name="ns"></param>
        /// <param name="rest"></param>
        /// <returns>columns and rest</returns>
        (BList<SqlValue>,BList<Physical>) ParseRestViewItem(long t,
            BList<SqlValue> ns,BList<Physical> rest)
        {
            if (tok == Sqlx.ID)
                (ns,rest) = ParseRestViewColDefin(t, ns, rest);
            else
                (ns,rest) = ParseRestViewConstraintDefin(t, ns, rest);
            return (ns,rest);
        }
        /// <summary>
        /// Called in 2 places (identified by value of rest)
        /// 1. processing of CREATE VIEW .. OF( .. RestView Definition: rest is empty virtual table def
        /// 2. load of database containing RestView: rest is null 
        /// Cases 1 and 2 need to parse the source, case 1 needs to define columns and VIndexes
        /// </summary>
        /// <param name="t"></param>
        /// <param name="ns"></param>
        /// <param name="rest"></param>
        /// <returns>columns and rest</returns>
        (BList<SqlValue>, BList<Physical>) ParseRestViewConstraintDefin(long t, 
            BList<SqlValue> ns, BList<Physical> rest)
        {
            Ident name;
            if (tok == Sqlx.CONSTRAINT)
            {
                Next();
                name = new Ident(this, t);
                Mustbe(Sqlx.ID);
            }
            else
                name = new Ident(this, t);
            Sqlx s = Mustbe(Sqlx.UNIQUE, Sqlx.PRIMARY, Sqlx.FOREIGN);
            Physical px = null;
            switch (s)
            {
                case Sqlx.UNIQUE: px = ParseUniqueConstraint(t, name); break;
                case Sqlx.PRIMARY: px = ParsePrimaryConstraint(t, name); break;
                case Sqlx.FOREIGN: px = ParseReferentialConstraint(t, name); break;
            }
            if (px != null && rest!=null)
                rest += px;
            cx.result = -1L;
            return (ns, rest);
        }
        /// <summary>
        /// Called in 2 places (identified by value of rest)
        /// 1. processing of CREATE VIEW .. OF( .. RestView Definition: rest is empty virtual table def
        /// 2. load of database containing RestView: rest is null 
        /// Cases 1 and 2 need to parse the source, case 1 needs to define columns and VIndexes
        /// </summary>
        /// <param name="t"></param>
        /// <param name="ns"></param>
        /// <param name="rest"></param>
        /// <returns>columns and rest</returns>
        /// <exception cref="DBException"></exception>
        (BList<SqlValue>,BList<Physical>) ParseRestViewColDefin(long t,
            BList<SqlValue> ns,BList<Physical> rest)
        {
            Domain type = null;
            Domain dom = null;
            if (Match(Sqlx.COLUMN))
                Next();
            var colname = new Ident(this, t);
            var lp = LexPos();
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.ID)
            {
                var op = cx.db.role.dbobjects[new Ident(this, t).ident];
                type = cx.db.objects[op] as Domain
                    ?? throw new DBException("42119", lxr.val.ToString());
                Next();
            }
            else if (Match(Sqlx.CHARACTER, Sqlx.CHAR, Sqlx.VARCHAR, Sqlx.NATIONAL, Sqlx.NCHAR,
                Sqlx.BOOLEAN, Sqlx.NUMERIC, Sqlx.DECIMAL,
                Sqlx.DEC, Sqlx.FLOAT, Sqlx.REAL, Sqlx.DOUBLE,
                Sqlx.INT, Sqlx.INTEGER, Sqlx.BIGINT, Sqlx.SMALLINT, Sqlx.PASSWORD,
                Sqlx.BINARY, Sqlx.BLOB, Sqlx.NCLOB, Sqlx.CLOB, Sqlx.XML,
                Sqlx.DATE, Sqlx.TIME, Sqlx.TIMESTAMP, Sqlx.INTERVAL,
                Sqlx.DOCUMENT, Sqlx.DOCARRAY, Sqlx.CHECK,
#if MONGO
                Sqlx.OBJECT, // v5.1
#endif
                Sqlx.ROW, Sqlx.TABLE, Sqlx.REF))
                type = ParseSqlDataType();
            dom = type;
            if (Match(Sqlx.ARRAY))
            {
                dom = (Domain)cx.Add(new Domain(cx.GetUid(), Sqlx.ARRAY, type));
                Next();
            }
            else if (Match(Sqlx.MULTISET))
            {
                dom = (Domain)cx.Add(new Domain(cx.GetUid(), Sqlx.MULTISET, type));
                Next();
            }
            if (dom == null)
                throw new DBException("42120", colname.ident);
            dom = (Domain)cx.Add(dom);
            var tb = (Table)cx.db.objects[t];
            var sv = new SqlValue(colname, dom, new BTree<long, object>(DBObject._From, t));
            cx.Add(sv);
            var cix = cx.Ix(sv.defpos);
            cx.defs += (colname, cix);
            cx.Add(sv);
            tb = (Table)cx.obs[t];
            ns += sv;
            while (Match(Sqlx.REFERENCES, Sqlx.UNIQUE, Sqlx.PRIMARY))
            {
                var px = ParseRestViewColConstraintDefin(t, (int)ns.Count - 1);
                if (rest != null)
                    rest += px;
            }
            return (ns, rest);
        }
        VIndex ParseRestViewColConstraintDefin(long t, int c)
        {
            var key = new CList<int>(c);
            var tr = cx.db as Transaction;
            VIndex px = null;
            switch (tok)
            {
                case Sqlx.REFERENCES:
                    {
                        Next();
                        var rn = lxr.val.ToString();
                        var rt = cx.db.GetTable(rn,cx.role)??
                            throw new DBException("42107", rn).Mix();
                        var ix = rt.FindPrimaryIndex(cx);
                        BList<long?> cols = null;
                        Mustbe(Sqlx.ID);
                        if (tok == Sqlx.LPAREN)
                            cols = ParseColsList(rt);
                        var ct = PIndex.ConstraintType.ForeignKey;
                        px = cx.tr.ReferentialConstraint(cx,
    (Table)cx.obs[t], new Ident("", cx.Ix(0)), key, rt, cols, ct, c);
                        break;
                    }
                case Sqlx.UNIQUE:
                    {
                        Next();
                        if (tr!=null)
                        px = new VIndex("", t, key, PIndex.ConstraintType.Unique, -1L,
                            tr.nextPos+c+1, cx);
                        break;
                    }
                case Sqlx.PRIMARY:
                    {
                        Next();
                        Mustbe(Sqlx.KEY);
                        if (tr!=null)
                        px = new VIndex("", t, key, PIndex.ConstraintType.PrimaryKey,
    -1L, tr.nextPos+c+1, cx);
                        break;
                    }
            }
            return px;
        }
*/
        /// <summary>
        /// Detect start of TableMetadata or ColumnMetatdata
        /// TableMetadata = ENTITY | PIE | HISTGORAM | LEGEND | LINE | POINTS | REFERRED |string | iri
        /// ColumnMetadata = ATTRIBUTE | X | Y | CAPTION |string | iri | REFERS
        /// </summary>
        /// <param name="kind">the kind of object</param>
        /// <returns>wheteher metadata follows</returns>
        bool StartMetadata(Sqlx kind)
        {
            switch (kind)
            {
                case Sqlx.TABLE: return Match(Sqlx.ENTITY, Sqlx.PIE, Sqlx.HISTOGRAM, Sqlx.LEGEND, Sqlx.LINE, 
                    Sqlx.POINTS, Sqlx.DROP,Sqlx.JSON, Sqlx.CSV, Sqlx.CHARLITERAL, Sqlx.RDFLITERAL, Sqlx.REFERRED, 
                    Sqlx.ETAG);
                case Sqlx.COLUMN: return Match(Sqlx.ATTRIBUTE, Sqlx.X, Sqlx.Y, Sqlx.CAPTION, Sqlx.DROP, 
                    Sqlx.CHARLITERAL, Sqlx.RDFLITERAL,Sqlx.REFERS);
                case Sqlx.FUNCTION: return Match(Sqlx.ENTITY, Sqlx.PIE, Sqlx.HISTOGRAM, Sqlx.LEGEND,
                    Sqlx.LINE, Sqlx.POINTS, Sqlx.DROP, Sqlx.JSON, Sqlx.CSV, Sqlx.INVERTS, Sqlx.MONOTONIC);
                case Sqlx.VIEW: return Match(Sqlx.ENTITY, Sqlx.URL, Sqlx.MIME, Sqlx.SQLAGENT, Sqlx.USER, 
                    Sqlx.PASSWORD,Sqlx.CHARLITERAL,Sqlx.RDFLITERAL,Sqlx.ETAG,Sqlx.MILLI);
                case Sqlx.TYPE: return Match(Sqlx.PREFIX, Sqlx.SUFFIX, Sqlx.NODETYPE, Sqlx.EDGETYPE);
                case Sqlx.ANY:
                    Match(Sqlx.DESC, Sqlx.URL, Sqlx.MIME, Sqlx.SQLAGENT, Sqlx.USER, Sqlx.PASSWORD,
                        Sqlx.ENTITY,Sqlx.PIE,Sqlx.HISTOGRAM,Sqlx.LEGEND,Sqlx.LINE,Sqlx.POINTS,Sqlx.REFERRED,
                        Sqlx.ETAG,Sqlx.ATTRIBUTE,Sqlx.X,Sqlx.Y,Sqlx.CAPTION,Sqlx.REFERS,Sqlx.JSON,Sqlx.CSV,
                        Sqlx.INVERTS,Sqlx.MONOTONIC, Sqlx.PREFIX, Sqlx.SUFFIX);
                    return !Match(Sqlx.EOF,Sqlx.RPAREN,Sqlx.COMMA,Sqlx.RBRACK,Sqlx.RBRACE);
                default: return Match(Sqlx.CHARLITERAL, Sqlx.RDFLITERAL);
            }
        }
        internal CTree<Sqlx,TypedValue> ParseMetadata(string s,int off,Sqlx kind)
        {
            lxr = new Lexer(cx, s, off);
            return ParseMetadata(kind);
        }
        /// <summary>
        /// Parse ([ADD]|DROP) Metadata
        /// </summary>
        /// <param name="tr">the database</param>
        /// <param name="ob">the object the metadata is for</param>
        /// <param name="kind">the metadata</param>
        internal CTree<Sqlx, TypedValue> ParseMetadata(Sqlx kind)
        {
            var drop = false;
            if (Match(Sqlx.ADD, Sqlx.DROP))
            {
                drop = tok == Sqlx.DROP;
                Next();
            }
            var m = CTree<Sqlx, TypedValue>.Empty;
            TypedValue ds = TNull.Value;
            TypedValue iri = TNull.Value;
            long iv = -1;
            var lp = tok == Sqlx.LPAREN;
            if (lp)
                Next();
            while (StartMetadata(kind))
            {
                var o = lxr.val;
                switch (tok)
                {
                    case Sqlx.CHARLITERAL:
                        ds = drop ? new TChar("") : o;
                        break;
                    case Sqlx.RDFLITERAL:
                        Next();
                        iri = drop ? new TChar("") : lxr.val;
                        break;
                    case Sqlx.INVERTS:
                        {
                            Next();
                            if (tok == Sqlx.EQL)
                                Next();
                            var x = cx.GetObject(o.ToString()) ??
                                throw new DBException("42108", lxr.val.ToString()).Pyrrho();
                            iv = x.defpos;
                            break;
                        }
                    case Sqlx.DESC:
                    case Sqlx.URL:
                        {
                            if (drop)
                                break;
                            var t = tok;
                            Next();
                            if (tok == Sqlx.EQL)
                                Next();
                            if (tok == Sqlx.CHARLITERAL || tok == Sqlx.RDFLITERAL)
                                m += (t, lxr.val);
                            break;
                        }
                    case Sqlx.HISTOGRAM:
                    case Sqlx.LINE:
                    case Sqlx.PIE:
                    case Sqlx.POINTS:
                        {
                            if (drop)
                                o = new TChar("");
                            m += (tok, o);
                            Next();
                            if (tok==Sqlx.LPAREN)
                            {
                                Next();
                                m += (Sqlx.X, lxr.val);
                                Mustbe(Sqlx.ID);
                                Mustbe(Sqlx.COMMA);
                                m += (Sqlx.Y, lxr.val);
                                Mustbe(Sqlx.ID);
                             } else
                                continue;
                            break;
                        }
                    case Sqlx.PREFIX:
                    case Sqlx.SUFFIX:
                        {
                            var tk = tok;
                            Next();
                            if (drop)
                                o = new TChar("");
                            else 
                            {
                                o = lxr.val;
                                Mustbe(Sqlx.ID);
                            }
                            m += (tk, o);
                            break;
                        }
                    case Sqlx.EDGETYPE:
                        {
                            m += (tok, o);
                            Next();
                            Mustbe(Sqlx.LPAREN);
                            var ln = lxr.val;
                            if (!cx.role.dbobjects.Contains(ln.ToString()))
                                throw new DBException("42161", "NodeType", ln);
                            m += (Sqlx.ARROWBASE, ln);
                            Mustbe(Sqlx.ID);
                            Mustbe(Sqlx.COMMA);
                            var an = lxr.val;
                            if (!cx.role.dbobjects.Contains(an.ToString()))
                                throw new DBException("42161", "NodeType", ln);
                            m += (Sqlx.ARROW, an);
                            Mustbe(Sqlx.ID);
                            Mustbe(Sqlx.RPAREN);
                            break;
                        }
                    default:
                        if (drop)
                            o = new TChar("");
                        m += (tok, o);
                        iv = -1L;
                        break;
                    case Sqlx.RPAREN:
                        break;
                }
                Next();
            }
            if (ds != TNull.Value && !m.Contains(Sqlx.DESC))
                m += (Sqlx.DESC, ds);
            if (iv != -1L)
                m += (Sqlx.INVERTS, new TInt(iv));
            if (iri != TNull.Value)
                m += (Sqlx.IRI, iri);
            return m;
        }
        /// <summary>
        /// GenerationRule =  GENERATED ALWAYS AS '('Value')' [ UPDATE '(' Assignments ')' ]
        /// |   GENERATED ALWAYS AS ROW (START|END) .
        /// </summary>
        /// <param name="rt">The expected type</param>
        GenerationRule ParseGenerationRule(long tc,Domain xp)
        {
            var gr = GenerationRule.None;
            var ox = cx.parse;
            if (Match(Sqlx.GENERATED))
            {
                Next();
                Mustbe(Sqlx.ALWAYS);
                Mustbe(Sqlx.AS);
                if (Match(Sqlx.ROW))
                {
                    Next();
                    gr = tok switch
                    {
                        Sqlx.START => new GenerationRule(Generation.RowStart),
                        Sqlx.END => new GenerationRule(Generation.RowEnd),
                        _ => throw new DBException("42161", "START or END", tok.ToString()).Mix(),
                    };
                    Next();
                }
                else
                {
                    var st = lxr.start;
                    var oc = cx.parse;
                    var gnv = ParseSqlValue(xp).Reify(cx);
                    var s = new string(lxr.input, st, lxr.start - st);
                    gr = new GenerationRule(Generation.Expression, s, gnv, tc);
                    cx.Add(gnv);
                    cx.parse = oc;
                }
            }
            cx.parse = ox;
            return gr;
        }
        /// <summary>
        /// Parse a columnconstraintdefinition
        /// ColumnConstraint = [ CONSTRAINT id ] ColumnConstraintDef
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="pc">the column (Level2)</param>
        /// <returns>the updated table</returns>
		TableColumn ParseColumnConstraintDefin(Table tb, PColumn2 pc, TableColumn tc)
        {
       //     Ident name = null;
            if (tok == Sqlx.CONSTRAINT)
            {
                Next();
          //      name = new Ident(this);
                Mustbe(Sqlx.ID);
            }
            if (tok == Sqlx.NOT)
            { 
                Next();
                if (pc != null)
                { 
                    pc.notNull = true;
                    tc += (Domain.NotNull, true);
                    cx.db += (tc.defpos, tc);
                }
                Mustbe(Sqlx.NULL);
            }
            else
                tc = ParseColumnConstraint(tb, tc);
            return tc;
        }
        /// <summary>
        /// ColumnConstraintDef = 	NOT NULL
        ///     |	PRIMARY KEY 
        ///     |	REFERENCES id [ Cols ] { ReferentialAction }
        /// 	|	UNIQUE 
        ///     |   CHECK SearchCondition .
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="tc">the table column (Level3)</param>
        /// <param name="name">the name of the constraint</param>
        /// <returns>the updated table</returns>
		TableColumn ParseColumnConstraint(Table tb, TableColumn tc)
        {
            if (cx.tr == null) throw new DBException("42105");
            var key = new Domain(-1L,cx,Sqlx.ROW,new BList<DBObject>(tc),1);
            string nm = "";
            switch (tok)
            {
                case Sqlx.SECURITY:
                    Next();
                    tc = (TableColumn)ParseClassification(tc); break;
                case Sqlx.REFERENCES:
                    {
                        Next();
                        var rn = lxr.val.ToString();
                        var ta = cx.GetObject(rn);
                        var rt = ((ta is NodeType et) ? cx._Ob(et.structure) :ta) as Table;
                        if (rt == null && ta is RestView rv && cx._Dom(rv) is Domain dr)
                            rt = (Table?)cx.db.objects[dr.structure];
                        if (rt==null) throw new DBException("42107", rn).Mix();
                        var cols = Domain.Row;
                        Mustbe(Sqlx.ID);
                        if (tok == Sqlx.LPAREN)
                            cols = ParseColsList(rt)??throw new DBException("42000");
                        string afn = "";
                        if (tok == Sqlx.USING)
                        {
                            Next();
                            int st = lxr.start;
                            if (tok == Sqlx.ID)
                            {
                                Next();
                                var ni = new Ident(this);
                                var dt = cx._Dom(tb) ?? throw new DBException("42105");
                                var pr = cx.GetProcedure(LexPos().dp,ni.ident, new CList<Domain>(dt))
                                    ??throw new DBException("42108",ni.ident);
                                afn = "\"" + pr.defpos + "\"";
                            }
                            else
                            {
                                Mustbe(Sqlx.LPAREN);
                                ParseSqlValueList(Domain.Content);
                                Mustbe(Sqlx.RPAREN);
                                afn = new string(lxr.input,st, lxr.start - st);
                            }
                        }
                        var ct = ParseReferentialAction();
                        cx.Add(cx.tr.ReferentialConstraint(cx, 
                            tb, "", key, rt, cols, ct, afn));
                        break;
                    }
                case Sqlx.CONSTRAINT:
                    {
                        Next();
                        nm = new Ident(this).ident;
                        Mustbe(Sqlx.ID);
                        if (tok != Sqlx.CHECK)
                            throw new DBException("42161", "CHECK",tok);
                        goto case Sqlx.CHECK;
                    }
                case Sqlx.CHECK:
                    {
                        Next();
                        tc = ParseColumnCheckConstraint(tb, tc, nm);
                        break;
                    }
                case Sqlx.DEFAULT:
                    {
                        Next();
                        tc = (TableColumn)cx.Add(
                            tc+(Domain.Default,ParseSqlValue(cx._Dom(tc)??Domain.Content).Eval(cx)??TNull.Value));
                        break;
                    }
                case Sqlx.UNIQUE:
                    {
                        Next();
                        var tr = cx.db as Transaction?? throw new DBException("2F003");
                        cx.Add(new PIndex(nm, tb, key, PIndex.ConstraintType.Unique, -1L,
                            tr.nextPos));
                        break;
                    }
                case Sqlx.PRIMARY:
                    {
                        var tn = tb.NameFor(cx);
                        if (tb.FindPrimaryIndex(cx) is not null)
                            throw new DBException("42147", tn).Mix();
                        Next();
                        Mustbe(Sqlx.KEY);
                        cx.Add(new PIndex(tn, tb, key, PIndex.ConstraintType.PrimaryKey, 
                            -1L, cx.db.nextPos));
                        break;
                    }
            }
            return tc;
        }
        /// <summary>
        /// TableConstraint = [CONSTRAINT id ] TableConstraintDef .
		/// TableConstraintDef = UNIQUE Cols
		/// |	PRIMARY KEY  Cols
		/// |	FOREIGN KEY Cols REFERENCES Table_id [ Cols ] { ReferentialAction } .
        /// </summary>
        /// <param name="tb">the table</param>
		Table ParseTableConstraintDefin(Table tb)
        {
            Ident? name = null;
            if (tok == Sqlx.CONSTRAINT)
            {
                Next();
                name = new Ident(this);
                Mustbe(Sqlx.ID);
            }
            else if (tok==Sqlx.ID)
                name = new Ident(this);
            Sqlx s = Mustbe(Sqlx.UNIQUE, Sqlx.PRIMARY, Sqlx.FOREIGN, Sqlx.CHECK);
            switch (s)
            {
                case Sqlx.UNIQUE: tb = ParseUniqueConstraint(tb, name); break;
                case Sqlx.PRIMARY: tb = ParsePrimaryConstraint(tb, name); break;
                case Sqlx.FOREIGN: tb = ParseReferentialConstraint(tb, name); break;
                case Sqlx.CHECK: tb = ParseTableConstraint(tb, name); break;
            }
            cx.result = -1L;
            return (Table)cx.Add(tb);
        }
        /// <summary>
        /// construct a unique constraint
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="name">the constraint name</param>
        /// <returns>the updated table</returns>
        Table ParseUniqueConstraint(Table tb, Ident? name)
        {
            var tr = cx.db as Transaction ?? throw new DBException("42105");
            if (ParseColsList(tb) is not Domain ks) throw new DBException("42161", "cols");
            return (Table)(cx.Add(new PIndex(name?.ident ?? "", tb, 
                ks,PIndex.ConstraintType.Unique, -1L, tr.nextPos)) ?? throw new DBException("42105"));
        }
        /// <summary>
        /// construct a primary key constraint
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="cl">the ident</param>
        /// <param name="name">the constraint name</param>
        Table ParsePrimaryConstraint(Table tb, Ident? name)
        {
            var tr = cx.db as Transaction ?? throw new DBException("42105");
            if (tb.FindPrimaryIndex(cx) is Level3.Index x)
                throw new DBException("42147", x.NameFor(cx)).Mix();
            Mustbe(Sqlx.KEY);
            if (ParseColsList(tb) is not Domain ks) throw new DBException("42161", "cols");
            return (Table)(cx.Add(new PIndex(name?.ident ?? "", tb, ks,
                PIndex.ConstraintType.PrimaryKey, -1L, tr.nextPos))
                ?? throw new DBException("42105"));
        }
        /// <summary>
        /// construct a referential constraint
        /// id [ Cols ] { ReferentialAction }
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="name">the constraint name</param>
        /// <returns>the updated table</returns>
        Table ParseReferentialConstraint(Table tb, Ident? name)
        {
            var tr = cx.db as Transaction ?? throw new DBException("42105");
            Mustbe(Sqlx.KEY);
            var cols = ParseColsList(tb) ?? throw new DBException("42161","cols");
            Mustbe(Sqlx.REFERENCES);
            var refname = new Ident(this);
            var rt = cx.GetObject(refname.ident) as Table??
                throw new DBException("42107", refname).Mix();
            Mustbe(Sqlx.ID);
            var refs = Domain.Row;
            PIndex.ConstraintType ct = PIndex.ConstraintType.ForeignKey;
            if (tok == Sqlx.LPAREN)
                refs = ParseColsList(rt)??Domain.Null;
            string afn = "";
            if (tok == Sqlx.USING)
            {
                Next();
                int st = lxr.start;
                if (tok == Sqlx.ID)
                {
                    var ic = new Ident(this);
                    Next();
                    var pr = cx.GetProcedure(LexPos().dp, ic.ident,Context.Signature(refs))
                        ??throw new DBException("42108",ic.ident);
                    afn = "\"" + pr.defpos + "\"";
                }
                else
                {
                    Mustbe(Sqlx.LPAREN);
                    ParseSqlValueList(Domain.Content);
                    Mustbe(Sqlx.RPAREN);
                    afn = new string(lxr.input, st, lxr.start - st);
                }
            }
            if (tok == Sqlx.ON)
                ct |= ParseReferentialAction();
            return (Table)(cx.Add(tr.ReferentialConstraint(cx, tb, name?.ident??"", cols, rt, refs, ct, afn))
                ?? throw new DBException("42105"));
        }
        /// <summary>
        /// CheckConstraint = [ CONSTRAINT id ] CHECK '(' [XMLOption] SqlValue ')' .
        /// </summary>
        /// <param name="tb">The table</param>
        /// <param name="name">the name of the constraint</param
        /// <returns>the new PCheck</returns>
        Table ParseTableConstraint(Table tb, Ident? name)
        {
            int st = lxr.start;
            var nst = cx.db.nextStmt;
            Mustbe(Sqlx.LPAREN);
            var se = ParseSqlValue(Domain.Bool);
            Mustbe(Sqlx.RPAREN);
            var n = name ?? new Ident(this);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            PCheck r = new(tb, n.ident, se, new string(lxr.input, st, lxr.start - st), nst, tr.nextPos, cx);
            tb = (Table)(cx.Add(r) ?? throw new DBException("42105"));
            if (tb.defpos < Transaction.TransPos && cx._Dom(tb) is Domain dt)
            {
                var trs = tb.RowSets(new Ident("", Iix.None),cx, dt,-1L);
                if (trs.First(cx) != null)
                    throw new DBException("44000", n.ident).ISO();
            }
            return tb;
        }
        /// <summary>
        /// CheckConstraint = [ CONSTRAINT id ] CHECK '(' [XMLOption] SqlValue ')' .
        /// </summary>
        /// <param name="tb">The table</param>
        /// <param name="pc">the column constrained</param>
        /// <param name="name">the name of the constraint</param>
        /// <returns>the new TableColumn</returns>
        TableColumn ParseColumnCheckConstraint(Table tb, TableColumn tc,string nm)
        {
            var oc = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            int st = lxr.start;
            Mustbe(Sqlx.LPAREN);
            // Set up the information for parsing the column check constraint
            var ix = cx.Ix(tb.defpos);
            cx.defs += (new Ident(tb.NameFor(cx), ix), ix);
            for (var b = cx.obs.PositionAt(tb.defpos)?.Next(); b != null; b = b.Next())
                if (b.value() is TableColumn x)
                    cx.defs += (x.NameFor(cx), cx.Ix(x.defpos), Ident.Idents.Empty);
            var nst = cx.db.nextStmt;
            var se = ParseSqlValue(Domain.Bool);
            Mustbe(Sqlx.RPAREN);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var pc = new PCheck2(tb, tc, nm, se,
                new string(lxr.input, st, lxr.start - st), nst, tr.nextPos, cx);
            cx.parse = oc;
            return (TableColumn)(cx.Add(pc) ?? throw new DBException("42105"));
        }
        /// <summary>
		/// ReferentialAction = {ON (DELETE|UPDATE) (CASCADE| SET DEFAULT|RESTRICT)} .
        /// </summary>
        /// <returns>constraint type flags</returns>
		PIndex.ConstraintType ParseReferentialAction()
        {
            PIndex.ConstraintType r = PIndex.Reference;
            while (tok == Sqlx.ON)
            {
                Next();
                Sqlx when = Mustbe(Sqlx.UPDATE, Sqlx.DELETE);
                Sqlx what = Mustbe(Sqlx.RESTRICT, Sqlx.CASCADE, Sqlx.SET, Sqlx.NO);
                if (what == Sqlx.SET)
                    what = Mustbe(Sqlx.DEFAULT, Sqlx.NULL);
                else if (what == Sqlx.NO)
                {
                    Mustbe(Sqlx.ACTION);
                    throw new DBException("42123").Mix();
                }
                if (when == Sqlx.UPDATE)
                {
                    r &= ~PIndex.Updates;
                    switch (what)
                    {
                        case Sqlx.CASCADE: r |= PIndex.ConstraintType.CascadeUpdate; break;
                        case Sqlx.DEFAULT: r |= PIndex.ConstraintType.SetDefaultUpdate; break;
                        case Sqlx.NULL: r |= PIndex.ConstraintType.SetNullUpdate; break;
                        case Sqlx.RESTRICT: r |= PIndex.ConstraintType.RestrictUpdate; break;
                    }
                }
                else
                {
                    r &= ~PIndex.Deletes;
                    switch (what)
                    {
                        case Sqlx.CASCADE: r |= PIndex.ConstraintType.CascadeDelete; break;
                        case Sqlx.DEFAULT: r |= PIndex.ConstraintType.SetDefaultDelete; break;
                        case Sqlx.NULL: r |= PIndex.ConstraintType.SetNullDelete; break;
                        case Sqlx.RESTRICT: r |= PIndex.ConstraintType.RestrictDelete; break;
                    }
                }
            }
            return r;
        }
        /// <summary>
        /// Called from ALTER and CREATE parsers. This is the first time we see the proc-clause,
        /// and we must parse it to add any physicals needed to declare Domains and Types 
        /// before we add the PProcedure/Alter physical.
        /// CREATE (PROCEDURE|FUNCTION) id '(' Parameters ')' [RETURNS Type] Body
        /// ALTER (PROCEDURE|FUNCTION) id '(' Parameters ')' [RETURNS Type] AlterBody
        /// </summary>
        /// <param name="func">whether it is a function</param>
        /// <param name="create">whether it is CREATE</param>
        /// <returns>the new Executable</returns>
        public void ParseProcedureClause(bool func, Sqlx create)
        {
            var op = cx.parse;
            var nst = cx.db.nextStmt;
            var n = new Ident(lxr.val.ToString(),
                new Iix(lxr.Position,cx,cx.db.nextPos)); // n.iix.dp will match pp.ppos
            cx.parse = ExecuteStatus.Compile;
            Mustbe(Sqlx.ID);
            int st = lxr.start;
            var ps = ParseParameters(n);
            var a = cx.Signature(ps);
            var pi = new ObInfo(n.ident,
                Grant.Privilege.Owner | Grant.Privilege.Execute | Grant.Privilege.GrantExecute);
            var rdt = func ? ParseReturnsClause(n) : Domain.Null;
            if (Match(Sqlx.EOF) && create == Sqlx.CREATE)
                throw new DBException("42000", "EOF").Mix();
            var pr = cx.GetProcedure(LexPos().dp,n.ident, a);
            PProcedure? pp = null;
            if (pr == null)
            {
                if (create == Sqlx.CREATE)
                {
                    // create a preliminary version of the PProcedure without parsing the body
                    // in case the procedure is recursive (the body is parsed below)
                    pp = new PProcedure(n.ident, ps,
                        rdt, pr, new Ident(lxr.input.ToString()??"", n.iix), nst, cx.db.nextPos, cx);
                    pr = new Procedure(pp.defpos, cx, n.ident, ps, rdt, cx.role.defpos,
                        new BTree<long,object>(DBObject.Definer,cx.role.defpos)
                        +(DBObject.Infos,new BTree<long,ObInfo>(cx.role.defpos,pi)));
                    cx.Add(pp);
                    cx.db += (pr,cx.db.loadpos);
                    if (cx.dbformat<51)
                        cx.digest += (n.iix.dp, (n.ident, n.iix.dp));
                }
                else
                    throw new DBException("42108", n.ToString()).Mix();
            }
            else
                if (create == Sqlx.CREATE)
                throw new DBException("42167", n.ident, (int)ps.Count).Mix();
            if (create == Sqlx.ALTER && tok == Sqlx.TO)
            {
                Next();
                Mustbe(Sqlx.ID);
                cx.db += (pr,cx.db.loadpos);
            }
            else if (create == Sqlx.ALTER && (StartMetadata(Sqlx.FUNCTION) || Match(Sqlx.ALTER, Sqlx.ADD, Sqlx.DROP)))
                ParseAlterBody(pr);
            else
            {
                var lp = LexPos();
                if (StartMetadata(Sqlx.FUNCTION))
                {
                    var m = ParseMetadata(Sqlx.FUNCTION);
                    new PMetadata3(n.ident, 0, pr, m, cx.db.nextPos);
                }
                var s = new Ident(new string(lxr.input, st, lxr.start - st),lp);
                if (tok != Sqlx.EOF && tok != Sqlx.SEMICOLON && cx._Dom(pr) is Domain dp)
                {
                    cx.AddParams(pr);
                    cx.Add(pr);
                    var bd = ParseProcedureStatement(dp)??throw new DBException("42000");
                    cx.Add(pr);
                    var ns = cx.db.nextStmt;
                    var fm = new Framing(cx,pp?.nst??ns);
                    s = new Ident(new string(lxr.input, st, lxr.start - st),lp);
                    if (pp != null)
                    {
                        pp.source = s;
                        pp.proc = bd.defpos;
                        pp.framing = fm;
                        if (cx.db.format < 51)
                            pp.digested = cx.digest;
                    }
                    pr += (DBObject._Framing, fm);
                    pr += (Procedure.Clause, s.ident);
                    pr += (Procedure.Body, bd.defpos);
                    if (pp!=null)
                        cx.Add(pp);
                    cx.Add(pr);
                    cx.result = -1L;
                    cx.parse = op;
                }
                if (create == Sqlx.CREATE)
                    cx.db += (pr,cx.db.loadpos);
                var cix = cx.Ix(pr.defpos);
                cx.defs += (n, cix);
                if (pp == null)
                    cx.Add(new Modify(pr.defpos, pr, s, nst, cx.db.nextPos, cx)); // finally add the Modify
            }
            cx.result = -1L;
            cx.parse = op;
        }
        internal (BList<long?>,Domain) ParseProcedureHeading(Ident pn)
        {
            var ps = BList<long?>.Empty;
            var oi = Domain.Null;
            if (tok != Sqlx.LPAREN)
                return (ps, Domain.Null);
            ps = ParseParameters(pn);
            LexPos(); // for synchronising with CREATE
            if (tok == Sqlx.RETURNS)
            {
                Next();
                oi = ParseSqlDataType(pn);
            }
            return (ps, oi);
        }
        /// <summary>
        /// Function metadata is used to control display of output from table-valued functions
        /// </summary>
        /// <param name="pr"></param>
        void ParseAlterBody(Procedure pr)
        {

            var dt = cx._Dom(pr);
            if (dt==null || dt.kind != Sqlx.Null)
                return;
            if (dt.Length==0)
                return;
            ParseAlterOp(pr);
            while (tok == Sqlx.COMMA)
            {
                Next();
                ParseAlterOp(pr);
            }
        }
        /// <summary>
        /// Parse a parameter list
        /// </summary>
        /// <param name="pn">The proc/method name</param>
        /// <param name="xp">The UDT if we are in CREATE TYPE (null if in CREATE/ALTER METHOD or if no udt)</param>
        /// <returns>the list of formal procparameters</returns>
		internal BList<long?> ParseParameters(Ident pn,Domain? xp = null)
		{
            Mustbe(Sqlx.LPAREN);
            var r = BList<long?>.Empty;
			while (tok!=Sqlx.RPAREN)
			{
                r+= ParseProcParameter(pn,xp).defpos;
				if (tok!=Sqlx.COMMA)
					break;
				Next();
			}
			Mustbe(Sqlx.RPAREN);
			return r;
		}
		internal Domain ParseReturnsClause(Ident pn)
		{
			if (tok!=Sqlx.RETURNS)
				return Domain.Null;
			Next();
            if (tok == Sqlx.ID)
            {
                var s = lxr.val.ToString();
                Next();
                var ob = cx._Ob(cx.role.dbobjects[s]??-1L);
                return cx._Dom(ob)??Domain.Null;
            }
			return (Domain)cx.Add(ParseSqlDataType(pn));
		}
        /// <summary>
        /// parse a formal parameter
        /// </summary>
        /// <param name="pn">The procedure or method</param>
        /// <param name="xp">The UDT if in CREATE TYPE</param>
        /// <returns>the procparameter</returns>
		FormalParameter ParseProcParameter(Ident pn,Domain? xp=null)
		{
			Sqlx pmode = Sqlx.IN;
			if (Match(Sqlx.IN,Sqlx.OUT,Sqlx.INOUT))
			{
				pmode = tok;
				Next();
			}
			var n = new Ident(this);
			Mustbe(Sqlx.ID);
            var p = new FormalParameter(n.iix.dp, pmode,n.ident, ParseSqlDataType(n))
                +(DBObject._From,pn.iix.dp);
            cx.db += (p, cx.db.loadpos);
            cx.Add(p);
            if (xp == null) // prepare to parse a body
                cx.defs += (new Ident(pn, n), pn.iix);
			if (Match(Sqlx.RESULT))
			{
                p += (FormalParameter.Result, true);
                cx.db += (p, cx.db.loadpos);
                cx.Add(p); 
                Next();
			}
            return p;
		}
        /// <summary>
		/// Declaration = 	DECLARE id { ',' id } Type
		/// |	DECLARE id CURSOR FOR QueryExpression [ FOR UPDATE [ OF Cols ]] 
        /// |   HandlerDeclaration .
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseDeclaration()
		{
            var lp = LexPos();
			Next();
 			if (Match(Sqlx.CONTINUE,Sqlx.EXIT,Sqlx.UNDO))
				return (Executable)cx.Add(ParseHandlerDeclaration());
			var n = new Ident(this);
			Mustbe(Sqlx.ID);
            LocalVariableDec lv;
            if (tok == Sqlx.CURSOR)
            {
                Next();
                Mustbe(Sqlx.FOR);
                var cs = ParseCursorSpecification(Domain.TableType);
                var cu = (RowSet?)cx.obs[cs.union]??throw new PEException("PE1557");
                var sc = new SqlCursor(n.iix.dp, cu, n.ident);
                cx.result = -1L;
                cx.Add(sc);
                lv = new CursorDeclaration(lp.dp, cx, sc, cu);
            }
            else
            {
                var ld = ParseSqlDataType();
                var vb = new SqlValue(n, ld);
                cx.Add(vb);
                lv = new LocalVariableDec(lp.dp, cx, vb);
                if (Match(Sqlx.EQL, Sqlx.DEFAULT))
                {
                    Next();
                    var iv = ParseSqlValue(ld);
                    cx.Add(iv);
                    lv += (LocalVariableDec.Init, iv.defpos);
                }
            }
            var cix = cx.Ix(lv.vbl);
            cx.defs += (n, cix);
            cx.Add(lv);
            return lv;
		}
        /// <summary>
        /// |	DECLARE HandlerType HANDLER FOR ConditionList Statement .
        /// HandlerType = 	CONTINUE | EXIT | UNDO .
        /// </summary>
        /// <returns>the Executable resulting from the parse</returns>
        Executable ParseHandlerDeclaration()
        {
            var hs = new HandlerStatement(LexDp(), tok, new Ident(this).ident);
            Mustbe(Sqlx.CONTINUE, Sqlx.EXIT, Sqlx.UNDO);
            Mustbe(Sqlx.HANDLER);
            Mustbe(Sqlx.FOR);
            hs+=(HandlerStatement.Conds,ParseConditionValueList());
            if (ParseProcedureStatement(Domain.Content) is not Executable a)
                throw new DBException("42161", "handler");
            cx.Add(a);
            hs= hs+(HandlerStatement.Action,a.defpos)+(DBObject.Dependents,a.dependents);
            return (Executable)cx.Add(hs);
        }
        /// <summary>
		/// ConditionList =	Condition { ',' Condition } .
        /// </summary>
        /// <returns>the list of conditions</returns>
        BList<string> ParseConditionValueList()
        {
            var r = new BList<string>(ParseConditionValue());
            while (tok == Sqlx.COMMA)
            {
                Next();
                r+=ParseConditionValue();
            }
            return r;
        }
        /// <summary>
		/// Condition =	Condition_id | SQLSTATE string | SQLEXCEPTION | SQLWARNING | (NOT FOUND) .
        /// </summary>
        /// <returns>a string</returns>
        string ParseConditionValue()
        {
			var n = lxr.val;
			if (tok==Sqlx.SQLSTATE)
			{
				Next();
                if (tok == Sqlx.VALUE)
                    Next();
				n = lxr.val;
				Mustbe(Sqlx.CHARLITERAL);
                switch (n.ToString()[..2])
                { // handlers are not allowed to defeat the transaction machinery
                    case "25": throw new DBException("2F003").Mix();
                    case "40": throw new DBException("2F003").Mix();
                    case "2D": throw new DBException("2F003").Mix();
                }
			} 
			else if (Match(Sqlx.SQLEXCEPTION,Sqlx.SQLWARNING,Sqlx.NOT))
			{
				if (tok==Sqlx.NOT)
				{
					Next();
					Mustbe(Sqlx.FOUND);
					n = new TChar("NOT_FOUND");
				}
				else
				{
					n = new TChar(tok.ToString());
					Next();
				}
			}
			else
				Mustbe(Sqlx.ID);
            return n.ToString();
        }
        /// <summary>
		/// CompoundStatement = Label BEGIN [XMLDec] Statements END .
        /// </summary>
        /// <param name="n">the label</param>
        /// <param name="f">The procedure whose body is being defined if any</param>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseCompoundStatement(Domain xp,string n)
        {
            var cs = new CompoundStatement(LexDp(), n);
            var r = BList<long?>.Empty;
            Mustbe(Sqlx.BEGIN);
            if (tok == Sqlx.TRANSACTION)
                throw new DBException("25001", "Nested transactions are not supported").ISO();
            if (tok == Sqlx.XMLNAMESPACES)
            {
                Next();
                r+=cx.Add(ParseXmlNamespaces()).defpos;
            }
            while (tok != Sqlx.END && ParseProcedureStatement(xp) is Executable a)
            {
                r+=cx.Add(a).defpos;
                if (tok == Sqlx.END)
                    break;
                Mustbe(Sqlx.SEMICOLON);
            }
            Mustbe(Sqlx.END);
            cs+=(CompoundStatement.Stms,r);
            cs += (DBObject.Dependents, _Deps(r));
            return (Executable)cx.Add(cs);
        }
        /// <summary>
		/// Statement = 	Assignment
		/// |	Call
		/// |	CaseStatement 
		/// |	Close
		/// |	CompoundStatement
		/// |	BREAK
		/// |	Declaration
		/// |	DeletePositioned
		/// |	DeleteSearched
		/// |	Fetch
		/// |	ForStatement
        /// |   GetDiagnostics
		/// |	IfStatement
		/// |	Insert
		/// |	ITERATE label
		/// |	LEAVE label
		/// |	LoopStatement
		/// |	Open
		/// |	Repeat
		/// |	RETURN TypedValue
		/// |	SelectSingle
		/// |	Raise
        /// |   Resignal
		/// |	Sparql
		/// |	UpdatePositioned
		/// |	UpdateSearched
		/// |	While .
        /// </summary>
        /// <param name="xp">the expected domain</param>
        /// <returns>the Executable resulting from the parse</returns>
		internal Executable? ParseProcedureStatement(Domain xp)
		{
            Match(Sqlx.BREAK);
            Executable? e;
 			switch (tok)
			{
                case Sqlx.EOF: return null;
                case Sqlx.ID: e = ParseLabelledStatement(xp); break;
                case Sqlx.BEGIN: e = ParseCompoundStatement(xp, ""); break;
                case Sqlx.CALL: e = ParseCallStatement(xp); break;
                case Sqlx.CASE: e = ParseCaseStatement(xp); break;
                case Sqlx.CLOSE: e = ParseCloseStatement(); break;
                case Sqlx.COMMIT: throw new DBException("2D000").ISO(); // COMMIT not allowed inside SQL routine
                case Sqlx.BREAK: e = ParseBreakLeave(); break;
                case Sqlx.DECLARE: e = ParseDeclaration(); break; // might be for a handler
                case Sqlx.DELETE: e = ParseSqlDelete(); break;
                case Sqlx.FETCH: e = ParseFetchStatement(); break;
                case Sqlx.FOR: e = ParseForStatement(xp, null); break;
                case Sqlx.GET: e = ParseGetDiagnosticsStatement(); break;
                case Sqlx.IF: e = ParseIfThenElse(xp); break;
                case Sqlx.INSERT: e = ParseSqlInsert(); break;
                case Sqlx.ITERATE: e = ParseIterate(); break;
                case Sqlx.LEAVE: e = ParseBreakLeave(); break;
                case Sqlx.LOOP: e = ParseLoopStatement(xp, null); break;
                case Sqlx.OPEN: e = ParseOpenStatement(); break;
                case Sqlx.REPEAT: e = ParseRepeat(xp, null); break;
                case Sqlx.ROLLBACK: e = new RollbackStatement(LexDp());
                    cx.Add(e);
                    break;
                case Sqlx.RETURN: e = ParseReturn(xp); break;
                case Sqlx.SELECT: e = ParseSelectSingle(new Ident(this),xp); break;
                case Sqlx.SET: e = ParseAssignment(); break;
                case Sqlx.SIGNAL: e = ParseSignal(); break;
                case Sqlx.RESIGNAL: e = ParseSignal(); break;
                case Sqlx.UPDATE: (cx,e) = ParseSqlUpdate(); break;
                case Sqlx.WHILE: e = ParseSqlWhile(xp, null); break;
				default: throw new DBException("42000",lxr.Diag).ISO();
			}
            cx.exec = e ?? throw new DBException("42000");
            return (Executable)cx.Add(e);
		}
        /// <summary>
        /// GetDiagnostics = GET DIAGMOSTICS Target = ItemName { , Target = ItemName }
        /// </summary>
        Executable ParseGetDiagnosticsStatement()
        {
            Next();
            Mustbe(Sqlx.DIAGNOSTICS);
            var r = new GetDiagnostics(LexDp());
            var d = 1;
            for (; ; )
            {
                var t = ParseSqlValueEntry(Domain.Content, false);
                d = Math.Max(d, t.depth + 1);
                cx.Add(t);
                Mustbe(Sqlx.EQL);
                Match(Sqlx.NUMBER, Sqlx.MORE, Sqlx.COMMAND_FUNCTION, Sqlx.COMMAND_FUNCTION_CODE,
                    Sqlx.DYNAMIC_FUNCTION, Sqlx.DYNAMIC_FUNCTION_CODE, Sqlx.ROW_COUNT,
                    Sqlx.TRANSACTIONS_COMMITTED, Sqlx.TRANSACTIONS_ROLLED_BACK,
                    Sqlx.TRANSACTION_ACTIVE, Sqlx.CATALOG_NAME,
                    Sqlx.CLASS_ORIGIN, Sqlx.COLUMN_NAME, Sqlx.CONDITION_NUMBER,
                    Sqlx.CONNECTION_NAME, Sqlx.CONSTRAINT_CATALOG, Sqlx.CONSTRAINT_NAME,
                    Sqlx.CONSTRAINT_SCHEMA, Sqlx.CURSOR_NAME, Sqlx.MESSAGE_LENGTH,
                    Sqlx.MESSAGE_OCTET_LENGTH, Sqlx.MESSAGE_TEXT, Sqlx.PARAMETER_MODE,
                    Sqlx.PARAMETER_NAME, Sqlx.PARAMETER_ORDINAL_POSITION,
                    Sqlx.RETURNED_SQLSTATE, Sqlx.ROUTINE_CATALOG, Sqlx.ROUTINE_NAME,
                    Sqlx.ROUTINE_SCHEMA, Sqlx.SCHEMA_NAME, Sqlx.SERVER_NAME, Sqlx.SPECIFIC_NAME,
                    Sqlx.SUBCLASS_ORIGIN, Sqlx.TABLE_NAME, Sqlx.TRIGGER_CATALOG,
                    Sqlx.TRIGGER_NAME, Sqlx.TRIGGER_SCHEMA);
                r+=(GetDiagnostics.List,r.list+(t.defpos,tok));
                Mustbe(Sqlx.NUMBER, Sqlx.MORE, Sqlx.COMMAND_FUNCTION, Sqlx.COMMAND_FUNCTION_CODE,
                    Sqlx.DYNAMIC_FUNCTION, Sqlx.DYNAMIC_FUNCTION_CODE, Sqlx.ROW_COUNT,
                    Sqlx.TRANSACTIONS_COMMITTED, Sqlx.TRANSACTIONS_ROLLED_BACK,
                    Sqlx.TRANSACTION_ACTIVE, Sqlx.CATALOG_NAME,
                    Sqlx.CLASS_ORIGIN, Sqlx.COLUMN_NAME, Sqlx.CONDITION_NUMBER,
                    Sqlx.CONNECTION_NAME, Sqlx.CONSTRAINT_CATALOG, Sqlx.CONSTRAINT_NAME,
                    Sqlx.CONSTRAINT_SCHEMA, Sqlx.CURSOR_NAME, Sqlx.MESSAGE_LENGTH,
                    Sqlx.MESSAGE_OCTET_LENGTH, Sqlx.MESSAGE_TEXT, Sqlx.PARAMETER_MODE,
                    Sqlx.PARAMETER_NAME, Sqlx.PARAMETER_ORDINAL_POSITION,
                    Sqlx.RETURNED_SQLSTATE, Sqlx.ROUTINE_CATALOG, Sqlx.ROUTINE_NAME,
                    Sqlx.ROUTINE_SCHEMA, Sqlx.SCHEMA_NAME, Sqlx.SERVER_NAME, Sqlx.SPECIFIC_NAME,
                    Sqlx.SUBCLASS_ORIGIN, Sqlx.TABLE_NAME, Sqlx.TRIGGER_CATALOG,
                    Sqlx.TRIGGER_NAME, Sqlx.TRIGGER_SCHEMA);
                if (tok != Sqlx.COMMA)
                    break;
                Next();
            }
            return (Executable)cx.Add(r + (DBObject._Depth,d));
        }
        /// <summary>
        /// Label =	[ label ':' ] .
        /// Some procedure statements have optional labels. We deal with these here
        /// </summary>
        /// <param name="xp">the expected ob type if any</param>
		Executable? ParseLabelledStatement(Domain xp)
        {
            Ident sc = new(this);
            var lp = LexPos();
            Mustbe(Sqlx.ID);
            // OOPS: according to SQL 2003 there MUST follow a colon for a labelled statement
            if (tok == Sqlx.COLON)
            {
                Next();
                var s = sc.ident;
                var e = tok switch
                {
                    Sqlx.BEGIN => ParseCompoundStatement(xp, s),
                    Sqlx.FOR => ParseForStatement(xp, s),
                    Sqlx.LOOP => ParseLoopStatement(xp, s),
                    Sqlx.REPEAT => ParseRepeat(xp, s),
                    Sqlx.WHILE => ParseSqlWhile(xp, s),
                    _ => throw new DBException("26000", s).ISO(),
                };
                return (Executable)cx.Add(e);
            }
            // OOPS: but we'q better allow a procedure call here for backwards compatibility
            else if (tok == Sqlx.LPAREN)
            {
                Next();
                var cp = LexPos();
                var ps = ParseSqlValueList(Domain.Content);
                Mustbe(Sqlx.RPAREN);
                var a = cx.Signature(ps);
                var pr = cx.GetProcedure(cp.dp, sc.ident, a) ??
                    throw new DBException("42108", sc.ident);
                var cs = new CallStatement(cp.dp,cx,pr,sc.ident,ps);
                return (Executable)cx.Add(cs);
            }
            // OOPS: and a simple assignment for backwards compatibility
            else if (cx.defs[sc] is Iix vp && cx.obs[vp.dp] is DBObject vb &&
                cx._Dom(vb) is Domain dm)
            {
                Mustbe(Sqlx.EQL);
                var va = ParseSqlValue(dm);
                var sa = new AssignmentStatement(lp.dp,vb,va);
                return (Executable)cx.Add(sa);
            }
            return null;
        }
        /// <summary>
		/// Assignment = 	SET Target '='  TypedValue { ',' Target '='  TypedValue }
		/// 	|	SET '(' Target { ',' Target } ')' '='  TypedValue .
        /// </summary>
		Executable ParseAssignment()
        {
            var lp = LexPos();
            Next();
            if (tok == Sqlx.LPAREN)
                return ParseMultipleAssignment();
            var vb = ParseVarOrColumn(Domain.Content);
            cx.Add(vb);
            Mustbe(Sqlx.EQL);
            var va = ParseSqlValue(cx._Dom(vb)??Domain.Content);
            var sa = new AssignmentStatement(lp.dp,vb,va);
            return (Executable)cx.Add(sa);
        }
        /// <summary>
        /// 	|	SET '(' Target { ',' Target } ')' '='  TypedValue .
        /// Target = 	id { '.' id } .
        /// </summary>
		Executable ParseMultipleAssignment()
        {
            Mustbe(Sqlx.EQL);
            var ids = ParseIDList();
            var v = ParseSqlValue(Domain.Content);
            cx.Add(v);
            var ma = new MultipleAssignment(LexDp(),cx,ids,v);
            if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction)
                cx = ma.Obey(cx);
            return (Executable)cx.Add(ma);
        }
        /// <summary>
        /// |	RETURN TypedValue
        /// </summary>
		Executable ParseReturn(Domain xp)
        {
            Next();
            var re = ParseSqlValue(xp);
            cx.Add(re);
            var rs = new ReturnStatement(cx.GetUid(),re);
            return (Executable)cx.Add(rs);
        }
        /// <summary>
		/// CaseStatement = 	CASE TypedValue { WHEN TypedValue THEN Statements }+ [ ELSE Statements ] END CASE
		/// |	CASE { WHEN SqlValue THEN Statements }+ [ ELSE Statements ] END CASE .
        /// </summary>
        /// <returns>the Executable resulting from the parse</returns>
		Executable ParseCaseStatement(Domain xp)
        {
            Next();
            if (tok == Sqlx.WHEN)
            {
                var ws = ParseWhenList(xp);
                var ss = BList<long?>.Empty;
                if (tok == Sqlx.ELSE)
                {
                    Next();
                    ss = ParseStatementList(xp);
                }
                var e = new SearchedCaseStatement(LexDp(),cx,ws,ss);
                Mustbe(Sqlx.END);
                Mustbe(Sqlx.CASE);
                cx.Add(e);
                return e;
            }
            else
            {
                var op = ParseSqlValue(Domain.Content);
                var ws = ParseWhenList(cx._Dom(op)??Domain.Content);
                var ss = BList<long?>.Empty;
                if (tok == Sqlx.ELSE)
                {
                    Next();
                    ss = ParseStatementList(xp);
                }
                Mustbe(Sqlx.END);
                Mustbe(Sqlx.CASE);
                var e = new SimpleCaseStatement(LexDp(),cx,op,ws,ss);
                cx.Add(e);
                cx.exec = e;
                return e;
            }
        }
        /// <summary>
        /// { WHEN SqlValue THEN Statements }
        /// </summary>
        /// <returns>the list of Whenparts</returns>
		BList<WhenPart> ParseWhenList(Domain xp)
		{
            var r = BList<WhenPart>.Empty;
            var dp = LexDp();
			while (tok==Sqlx.WHEN)
			{
				Next();
                var c = ParseSqlValue(xp);
				Mustbe(Sqlx.THEN);
                r+=new WhenPart(dp, c, ParseStatementList(xp));
			}
			return r;
		}
        /// <summary>
		/// ForStatement =	Label FOR [ For_id AS ][ id CURSOR FOR ] QueryExpression DO Statements END FOR [Label_id] .
        /// </summary>
        /// <param name="n">the label</param>
        /// <param name="xp">The type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseForStatement(Domain xp,string? n)
        {
            var lp = LexDp();
            Next();
            Ident c = new(DBObject.Uid(lp), cx.Ix(lp));
            var d = 1; // depth
            if (tok != Sqlx.SELECT)
            {
                c = new Ident(this);
                Mustbe(Sqlx.ID);
                if (tok == Sqlx.CURSOR)
                {
                    Next();
                    Mustbe(Sqlx.FOR);
                }
                else
                {
                    Mustbe(Sqlx.AS);
                    if (tok != Sqlx.SELECT)
                    {
                        Mustbe(Sqlx.ID);
                        Mustbe(Sqlx.CURSOR);
                        Mustbe(Sqlx.FOR);
                    }
                }
            }
            var ss = ParseCursorSpecification(Domain.TableType,null,null,true); // use ambient declarations
            d = Math.Max(d, ss.depth + 1);
            var cs = (RowSet?)cx.obs[ss.union]??throw new DBException("42000");
            Mustbe(Sqlx.DO);
            var xs = ParseStatementList(xp);
            var fs = new ForSelectStatement(lp,cx,n??"",c,cs,xs) + (DBObject._Depth,d);
            Mustbe(Sqlx.END);
            Mustbe(Sqlx.FOR);
            if (tok == Sqlx.ID)
            {
                if (n != null && n != lxr.val.ToString())
                    throw new DBException("42157", lxr.val.ToString(), n).Mix();
                Next();
            }
            return (Executable)cx.Add(fs);
        }
        /// <summary>
		/// IfStatement = 	IF BooleanExpr THEN Statements { ELSEIF BooleanExpr THEN Statements } [ ELSE Statements ] END IF .
        /// </summary>
        /// <param name="xp">The type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseIfThenElse(Domain xp)
        {
            var lp = LexDp();
            var old = cx;
            Next();
            var se = ParseSqlValue(Domain.Bool);
            cx.Add(se);
            Mustbe(Sqlx.THEN);
            var th = ParseStatementList(xp);
            var ei = BList<long?>.Empty;
            while (Match(Sqlx.ELSEIF))
            {
                var d = LexDp();
                Next();
                var s = ParseSqlValue(Domain.Bool);
                cx.Add(s);
                Mustbe(Sqlx.THEN);
                Next();
                var t = ParseStatementList(xp);
                var e = new IfThenElse(d, cx, s, t, BList<long?>.Empty, BList<long?>.Empty);
                cx.Add(e);
                ei += e.defpos;
            }
            var el = BList<long?>.Empty;
            if (tok == Sqlx.ELSE)
            {
                Next();
                el = ParseStatementList(xp);
            }
            Mustbe(Sqlx.END);
            Mustbe(Sqlx.IF);
            var ife = new IfThenElse(lp,cx,se, th,ei,el);
            cx = old;
            return (Executable)cx.Add(ife);
        }
        /// <summary>
		/// Statements = 	Statement { ';' Statement } .
        /// </summary>
        /// <param name="xp">The obs type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
		BList<long?> ParseStatementList(Domain xp)
		{
            if (ParseProcedureStatement(xp) is not Executable a)
                throw new DBException("42161", "statement");
            var r = new BList<long?>(cx.Add(a).defpos);
            while (tok==Sqlx.SEMICOLON)
			{
				Next();
                if (ParseProcedureStatement(xp) is not Executable b)
                    throw new DBException("42161", "statement");
                r +=cx.Add(b).defpos;
			}
			return r;
		}
        /// <summary>
		/// SelectSingle =	[DINSTINCT] SelectList INTO VariableRef { ',' VariableRef } TableExpression .
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseSelectSingle(Ident id,Domain xp)
        {
            cx.IncSD(id);
            Next();
            //     var ss = new SelectSingle(LexPos());
            //     var qe = new RowSetExpr(lp+1);
            //     var s = new RowSetSpec(lp+2,cx,xp) + 
            //                  (QueryExpression._Distinct, ParseDistinctClause())
            var d = ParseDistinctClause();
            var dm = ParseSelectList(id.iix.dp,xp);
            Mustbe(Sqlx.INTO);
            var ts = ParseTargetList();
      //      cs = cs + ;
      //      qe+=(RowSetExpr._Left,cx.Add(s).defpos);
       //     ss += (SelectSingle.Outs,ts);
            if (ts.Count != dm.rowType.Length)
                throw new DBException("22007").ISO();
            //      s+=(RowSetSpec.TableExp,ParseTableExpression(s).defpos);
            RowSet te = ParseTableExpression(id.iix,dm,xp);
            if (d)
                te = new DistinctRowSet(cx, te);
            var cs = new SelectStatement(id.iix.dp, te);
            var ss = new SelectSingle(id.iix.dp)+(ForSelectStatement.Sel,cs);
            cx.DecSD();
            cx.exec = ss;
            return (Executable)cx.Add(ss);
        }
        /// <summary>
        /// traverse a comma-separated variable list
        /// </summary>
        /// <returns>the list</returns>
		BList<long?> ParseTargetList()
		{
			bool b = (tok==Sqlx.LPAREN);
                if (b)
                    Next();
            var r = BList<long?>.Empty;
            for (; ; )
            {
                var v = ParseVarOrColumn(Domain.Content);
                cx.Add(v);
                r += v.defpos;
                if (tok != Sqlx.COMMA)
                    break;
                Next();
            }
			if (b)
				Mustbe(Sqlx.RPAREN);
			return r;
		}
        /// <summary>
        /// parse a dotted identifier chain. Watch for pseudo TableColumns
        /// CHECK ROW PARTITION VERSIONING PROVENANCE TYPE_URI SYSTEM_TIME
        /// The result will get classified as variable or ident
        /// during the Analysis stage Selects when things get setup
        /// </summary>
        /// <param name="ppos">the lexer position</param>
        /// <returns>an sqlName </returns>
        SqlValue ParseVarOrColumn(Domain xp)
        {
            Match(Sqlx.SYSTEM_TIME, Sqlx.SECURITY);
            if (tok == Sqlx.SECURITY)
            {
                if (cx.db.user?.defpos != cx.db.owner)
                    throw new DBException("42105");
                var sp = LexPos();
                Next();
                return (SqlValue)cx.Add(new SqlFunction(sp.dp, cx, Sqlx.SECURITY, null, null, null, Sqlx.NO));
            }
            if (Match(Sqlx.PARTITION, Sqlx.POSITION, Sqlx.VERSIONING, Sqlx.CHECK, 
                Sqlx.SYSTEM_TIME, Sqlx.LAST_DATA))
            {
                SqlValue ps = new SqlFunction(LexPos().dp, cx, tok, null, null, null, Sqlx.NO);
                Next();
                if (tok == Sqlx.LPAREN && ((SqlFunction)ps).kind == Sqlx.VERSIONING)
                {
                    var vp = LexPos();
                    Next();
                    if (tok == Sqlx.SELECT)
                    {
                        var cs = ParseCursorSpecification(Domain.Null).union;
                        Mustbe(Sqlx.RPAREN);
                        var sv = (SqlValue)cx.Add(new SqlValueSelect(vp.dp, cx, 
                            (RowSet?)cx.obs[cs]??throw new DBException("42000"), xp));
                        ps += (SqlFunction._Val, sv);
                    } else
                        Mustbe(Sqlx.RPAREN);
                }
                return (SqlValue)cx.Add(ps);
            }
            var ttok = tok;
            Ident ic = ParseIdentChain();
            var lp = LexPos();
            if (tok == Sqlx.LPAREN)
            {
                Next();
                var ps = BList<long?>.Empty;
                if (tok != Sqlx.RPAREN)
                    ps = ParseSqlValueList(Domain.Content);
                Mustbe(Sqlx.RPAREN);
                var n = cx.Signature(ps);
                if (ic.Length == 0 || ic[ic.Length - 1] is not Ident pn)
                    throw new DBException("42000");
                if (ic.Length == 1)
                {
                    var pr = cx.GetProcedure(LexPos().dp, pn.ident, n);
                    if (pr == null && (cx.db.objects[cx.role.dbobjects[pn.ident]??-1L]
                        ?? StandardDataType.Get(ttok)) is Domain ut)
                    {
                        cx.Add(ut);
                        var oi = ut.infos[cx.role.defpos];
                        if (cx.db.objects[oi?.methodInfos[pn.ident]?[n] ?? -1L] is Method me)
                        {
                            var ca = new CallStatement(lp.dp, cx, me, pn.ident, ps, null);
                            cx.Add(ca);
                            return new SqlConstructor(pn.iix.dp, cx, ut, ca);
                        }
                        if (cx.Signature(ut.rowType).CompareTo(n)==0 || ttok!=Sqlx.ID || ut.rowType==BList<long?>.Empty)
                            return new SqlDefaultConstructor(pn.iix.dp, cx, ut, ps);
                    }
                    if (pr == null)
                        throw new DBException("42108", ic.ident);
                    var cs = new CallStatement(lp.dp, cx, pr, pn.ident, ps);
                    cx.Add(cs);
                    return (SqlValue)cx.Add(new SqlProcedureCall(pn.iix.dp, cs));
                }
                else if (ic.Prefix(ic.Length-2) is Ident pf)
                {
                    var vr = (SqlValue)Identify(pf, Domain.Content);
                    var ms = new CallStatement(lp.dp, cx, pn.ident, ps, vr); // leavingNode the proc null for now
                    cx.Add(ms);
                    return (SqlValue)cx.Add(new SqlMethodCall(pn.iix.dp, ms));
                }
            }
            var ob = Identify(ic, xp);
            if (ob is not SqlValue r)
                throw new DBException("42112", ic.ToString());
            return r;
        }
        DBObject Identify(Ident ic, Domain xp)
        {
            if (cx.user == null)
                throw new DBException("42105");
            // See SourceIntro.docx section 6.1.2
            // we look up the identifier chain ic
            // and perform 6.1.2 (2) if we find anything
            var len = ic.Length;
            var (pa, sub) = cx.Lookup(LexPos().dp, ic, len);
            // pa is the object that was found, or null
            // if sub is non-zero there is a new chain to construct
            var m = sub?.Length ?? 0;
            var nm = len - m;
            DBObject ob;
            // nm is the position  of the first such in the chain ic
            // create the missing components if any (see 6.1.2 (1))
            for (var i = len - 1; i >= nm; i--)
                if (ic[i] is Ident c)
                {// the ident of the component to create
                    if (i == len - 1)
                    {
                        ob = new SqlValue(c, xp) ?? throw new PEException("PE1561");
                        cx.Add(ob);
                        // cx.defs enables us to find these objects again
                        cx.defs += (c, 1);
                        cx.defs += (ic, ic.Length);
                        cx.undefined += (ob.defpos, true);
                        pa = ob;
                    }
                    else
                    {
                        var pd = cx._Dom(pa) ?? (Domain)Domain.Row.Relocate(cx.GetUid());
                        new ForwardReference(c, cx,
                            BTree<long, object>.Empty
                            + (DBObject._Domain, pd.defpos)
                            + (DBObject._Depth, i + 1));
                    }
                }
            if (pa == null)
                throw new PEException("PE1562");
            if (pa.defpos >= Transaction.Executables && ic.iix.dp < Transaction.Executables)
            {
                var nv = pa.Relocate(ic.iix.dp);
                cx.Replace(pa, nv);
                return nv;
            }
            return pa;
        }
        /// <summary>
        /// Parse an identifier
        /// </summary>
        /// <returns>the Ident</returns>
       Ident ParseIdent()
        {
            var c = new Ident(this);
            Mustbe(Sqlx.ID, Sqlx.PARTITION, Sqlx.POSITION, Sqlx.VERSIONING, Sqlx.CHECK,
                Sqlx.SYSTEM_TIME);
            return c;
        }
        /// <summary>
        /// Parse a IdentChain
        /// </summary>
        /// <returns>the Ident</returns>
		Ident ParseIdentChain() 
		{
            var left = ParseIdent();
			if (tok==Sqlx.DOT)
			{
				Next();
                if (!Match(Sqlx.ID)) // allow VERSIONING etc to follow - see  ParseVarOrColum
                    return left;
                left = new Ident(left, ParseIdentChain());
			}
			return left;
		}
        /// <summary>
		/// LoopStatement =	Label LOOP Statements END LOOP .
        /// </summary>
        /// <param name="n">the label</param>
        /// <param name="xp">The  type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseLoopStatement(Domain xp, string? n)
        {
            var ls = new LoopStatement(LexDp(), n??"",cx.cxid);
                Next();
                ls+=(WhenPart.Stms,ParseStatementList(xp));
                Mustbe(Sqlx.END);
                Mustbe(Sqlx.LOOP);
                if (tok == Sqlx.ID && n != null && n == lxr.val.ToString())
                    Next();
            return (Executable)cx.Add(ls);
        }
        /// <summary>
		/// While =		Label WHILE SqlValue DO Statements END WHILE .
        /// </summary>
        /// <param name="n">the label</param>
        /// <param name="xp">The type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseSqlWhile(Domain xp, string? n)
        {
            var ws = new WhileStatement(LexDp(), n??"");
            var old = cx; // new SaveContext(lxr, ExecuteStatus.Parse);
            Next();
            var s = ParseSqlValue(Domain.Bool);
            cx.Add(s);
            ws+=(WhileStatement.Search,s.defpos);
            Mustbe(Sqlx.DO);
            ws+=(WhileStatement.What,ParseStatementList(xp));
            Mustbe(Sqlx.END);
            Mustbe(Sqlx.WHILE);
            if (tok == Sqlx.ID && n != null && n == lxr.val.ToString())
                Next();
            cx = old; // old.Restore(lxr);
            cx.exec = ws;
            return (Executable)cx.Add(ws);
        }
        /// <summary>
		/// Repeat =		Label REPEAT Statements UNTIL BooleanExpr END REPEAT .
        /// </summary>
        /// <param name="n">the label</param>
        /// <param name="xp">The obs type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseRepeat(Domain xp, string? n)
        {
            var rs = new RepeatStatement(LexDp(), n??"");
            Next();
            rs+=(WhileStatement.What,ParseStatementList(xp));
            Mustbe(Sqlx.UNTIL);
            var s = ParseSqlValue(Domain.Bool);
            cx.Add(s);
            rs+=(WhileStatement.Search,s.defpos);
            Mustbe(Sqlx.END);
            Mustbe(Sqlx.REPEAT);
            if (tok == Sqlx.ID && n != null && n == lxr.val.ToString())
                Next();
            cx.exec = rs;
            return (Executable)cx.Add(rs);
        }
        /// <summary>
        /// Parse a break or leave statement
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseBreakLeave()
		{
			Sqlx s = tok;
			Ident? n = null;
			Next();
			if (s==Sqlx.LEAVE && tok==Sqlx.ID)
			{
				n = new Ident(this);
				Next();
			}
			return (Executable)cx.Add(new BreakStatement(LexDp(),n?.ident)); 
		}
        /// <summary>
        /// Parse an iterate statement
        /// </summary>
        /// <param name="f">The procedure whose body is being defined if any</param>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseIterate()
		{
			Next();
			var n = new Ident(this);
			Mustbe(Sqlx.ID);
			return (Executable)cx.Add(new IterateStatement(LexDp(), n.ident)); 
		}
        /// <summary>
        /// |	SIGNAL (id|SQLSTATE [VALUE] string) [SET item=Value {,item=Value} ]
        /// |   RESIGNAL [id|SQLSTATE [VALUE] string] [SET item=Value {,item=Value} ]
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseSignal()
        {
            Sqlx s = tok;
            TypedValue n;
            Next();
            if (tok == Sqlx.ID)
            {
                n = lxr.val;
                Next();
            }
            else
            {
                Mustbe(Sqlx.SQLSTATE);
                if (tok == Sqlx.VALUE)
                    Next();
                n = lxr.val;
                Mustbe(Sqlx.CHARLITERAL);
            }
            var r = new SignalStatement(LexDp(), n.ToString()) + (SignalStatement.SType, s);
            if (tok == Sqlx.SET)
            {
                Next();
                for (; ; )
                {
                    Match(Sqlx.CLASS_ORIGIN, Sqlx.SUBCLASS_ORIGIN, Sqlx.CONSTRAINT_CATALOG,
                        Sqlx.CONSTRAINT_SCHEMA, Sqlx.CONSTRAINT_NAME, Sqlx.CATALOG_NAME,
                        Sqlx.SCHEMA_NAME, Sqlx.TABLE_NAME, Sqlx.COLUMN_NAME, Sqlx.CURSOR_NAME,
                        Sqlx.MESSAGE_TEXT);
                    var k = tok;
                    Mustbe(Sqlx.CLASS_ORIGIN, Sqlx.SUBCLASS_ORIGIN, Sqlx.CONSTRAINT_CATALOG,
                        Sqlx.CONSTRAINT_SCHEMA, Sqlx.CONSTRAINT_NAME, Sqlx.CATALOG_NAME,
                        Sqlx.SCHEMA_NAME, Sqlx.TABLE_NAME, Sqlx.COLUMN_NAME, Sqlx.CURSOR_NAME,
                        Sqlx.MESSAGE_TEXT);
                    Mustbe(Sqlx.EQL);
                    var sv = ParseSqlValue(Domain.Content);
                    cx.Add(sv);
                    r += (SignalStatement.SetList, r.setlist + (k, sv.defpos));
                    if (tok != Sqlx.COMMA)
                        break;
                    Next();
                }
            }
            cx.exec = r;
            return (Executable)cx.Add(r);
        }
        /// <summary>
		/// Open =		OPEN id .
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
 		Executable ParseOpenStatement()
		{
			Next();
			var o = new Ident(this);
			Mustbe(Sqlx.ID);
			return (Executable)cx.Add(new OpenStatement(LexDp(),
                cx.Get(o, Domain.TableType) as SqlCursor
                ?? throw new DBException("34000",o.ToString()),
                cx.cxid));
		}
        /// <summary>
		/// Close =		CLOSE id .
        /// </summary>
        /// <returns>The Executable result of the parse</returns>
		Executable ParseCloseStatement()
		{
			Next();
			var o = new Ident(this);
			Mustbe(Sqlx.ID);
			return (Executable)cx.Add(new CloseStatement(LexDp(),
                cx.Get(o, Domain.TableType) as SqlCursor
                ?? throw new DBException("34000", o.ToString()),
                cx.cxid));
		}
        /// <summary>
		/// Fetch =		FETCH Cursor_id INTO VariableRef { ',' VariableRef } .
        /// </summary>
        /// <returns>The Executable result of the parse</returns>
        Executable ParseFetchStatement()
        {
            Next();
            var dp = LexDp();
            var how = Sqlx.NEXT;
            SqlValue? where = null;
            if (Match(Sqlx.NEXT, Sqlx.PRIOR, Sqlx.FIRST, 
                Sqlx.LAST, Sqlx.ABSOLUTE, Sqlx.RELATIVE))
            {
                how = tok;
                Next();
            }
            if (how == Sqlx.ABSOLUTE || how == Sqlx.RELATIVE)
                where = ParseSqlValue(Domain.Int);
            if (tok == Sqlx.FROM)
                Next();
            var o = new Ident(this);
            Mustbe(Sqlx.ID);
            var fs = new FetchStatement(dp, 
                cx.Get(o, Domain.TableType) as SqlCursor 
                ?? throw new DBException("34000", o.ToString()),
                how, where);
            Mustbe(Sqlx.INTO);
            fs+=(FetchStatement.Outs,ParseTargetList());
            return (Executable)cx.Add(fs);
        }
        /// <summary>
        /// [ TriggerCond ] (Call | (BEGIN ATOMIC Statements END)) .
		/// TriggerCond = WHEN '(' SqlValue ')' .
        /// </summary>
        /// <returns>a TriggerAction</returns>
        internal long ParseTriggerDefinition(PTrigger trig)
        {
            long oldStart = cx.parseStart; // safety
            cx.parse = ExecuteStatus.Parse;
            cx.parseStart = LexPos().lp;
            var op = cx.parse;
            var fn = new Ident(cx.NameFor(trig.target), LexPos());
            var ta = cx.db.objects[trig.target];
            var tb = ((ta is NodeType et) ? cx._Ob(et.structure):ta) as Table 
                ?? throw new PEException("PE1562");
            tb = (Table)cx.Add(tb);
            if (cx._Dom(tb) is not Domain dt)
                throw new PEException("PE47131");
            var fm = tb.RowSets(fn, cx, dt,fn.iix.dp);
            trig.from = fm.defpos;
            if (cx._Dom(fm) is not Domain df)
                throw new PEException("PE47132");
            trig.dataType = df;
            var tg = new Trigger(trig,cx.role);
            cx.Add(tg); // incomplete version for parsing
            if (trig.oldTable != null)
            {
                var tt = (TransitionTable)cx.Add(new TransitionTable(trig.oldTable, true, cx, fm, tg));
                var nix = new Iix(trig.oldTable.iix, tt.defpos);
                cx.defs += (trig.oldTable,nix);
            }
            if (trig.oldRow != null)
            {
                cx.Add(new SqlOldRow(trig.oldRow, cx, fm));
                cx.defs += (trig.oldRow, trig.oldRow.iix);
            }
            if (trig.newTable != null)
            {
                var tt = (TransitionTable)cx.Add(new TransitionTable(trig.newTable, true, cx, fm, tg));
                var nix = new Iix(trig.newTable.iix, tt.defpos);
                cx.defs += (trig.newTable,nix);
            }
            if (trig.newRow != null)
            {
                cx.Add(new SqlNewRow(trig.newRow, cx, fm));
                cx.defs += (trig.newRow, trig.newRow.iix);
            }
            for (var b = trig.dataType.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p)
                {
                    var px = new Iix(fn.iix, p);
                    cx.defs += (cx.NameFor(p), px, Ident.Idents.Empty);
                }
            SqlValue? when = null;
            Executable? act;
            if (tok == Sqlx.WHEN)
            {
                Next();
                when = ParseSqlValue(Domain.Bool);
            }
            if (tok == Sqlx.BEGIN)
            {
                Next();
                if (new CompoundStatement(LexDp(),"") is not CompoundStatement cs)
                    throw new DBException("42161","CompoundStatement");
                var ss = BList<long?>.Empty;
                Mustbe(Sqlx.ATOMIC);
                while (tok != Sqlx.END)
                {
                    if (ParseProcedureStatement(Domain.Content) is not Executable a)
                        throw new DBException("42161", "statement");
                    ss+=cx.Add(a).defpos; 
                    if (tok == Sqlx.END)
                        break;
                    Mustbe(Sqlx.SEMICOLON);
                }
                Next();
                cs+=(CompoundStatement.Stms,ss);
                cs += (DBObject.Dependents, _Deps(ss));
                act = cs;
            }
            else
                act = ParseProcedureStatement(Domain.Content)??
                    throw new DBException("42161","statement");
            cx.Add(act);
            var r = (WhenPart)cx.Add(new WhenPart(LexDp(), 
                when??SqlNull.Value, new BList<long?>(act.defpos)));
            trig.def = r.defpos;
            trig.framing = new Framing(cx,trig.nst);
            cx.Add(tg + (Trigger.Action, r.defpos));
            cx.parseStart = oldStart;
            cx.result = -1L;
            cx.parse = op;
            return r.defpos;
        }
        /// <summary>
        /// |	CREATE TRIGGER id (BEFORE|AFTER) Event ON id [ RefObj ] Trigger
        /// RefObj = REFERENCING  { (OLD|NEW)[ROW|TABLE][AS] id } .
        /// Trigger = FOR EACH ROW ...
        /// </summary>
        /// <returns>the executable</returns>
        void ParseTriggerDefClause()
        {
            var op = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            var nst = cx.db.nextStmt;
            var trig = new Ident(this);
            Mustbe(Sqlx.ID);
            PTrigger.TrigType tgtype = 0;
            var w = Mustbe(Sqlx.BEFORE, Sqlx.INSTEAD, Sqlx.AFTER);
            switch (w)
            {
                case Sqlx.BEFORE: tgtype |= PTrigger.TrigType.Before; break;
                case Sqlx.AFTER: tgtype |= PTrigger.TrigType.After; break;
                case Sqlx.INSTEAD: Mustbe(Sqlx.OF); tgtype |= PTrigger.TrigType.Instead; break;
            }
            tgtype = ParseTriggerHow(tgtype);
            var cls = BList<Ident>.Empty;
            var upd = (tgtype & PTrigger.TrigType.Update) == PTrigger.TrigType.Update;
            if (upd && tok == Sqlx.OF)
            {
                Next();
                cls += new Ident(this);
                Mustbe(Sqlx.ID);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    cls += new Ident(this);
                    Mustbe(Sqlx.ID);
                }
            }
            Mustbe(Sqlx.ON);
            var tabl = new Ident(this);
            Mustbe(Sqlx.ID);
            var ta = cx.GetObject(tabl.ident);
            if (ta is NodeType no)
                ta = cx._Ob(no.structure);
            var tb = ta as Table ?? throw new DBException("42107", tabl.ToString()).Mix();
            Ident? or = null, nr = null, ot = null, nt = null;
            if (tok == Sqlx.REFERENCING)
            {
                Next();
                while (tok == Sqlx.OLD || tok == Sqlx.NEW)
                {
                    if (tok == Sqlx.OLD)
                    {
                        if ((tgtype & PTrigger.TrigType.Insert) == PTrigger.TrigType.Insert)
                            throw new DBException("42146", "OLD", "INSERT").Mix();
                        Next();
                        if (tok == Sqlx.TABLE)
                        {
                            Next();
                            if (ot != null)
                                throw new DBException("42143", "OLD").Mix();
                            if (tok == Sqlx.AS)
                                Next();
                            ot = new Ident(this);
                            Mustbe(Sqlx.ID);
                            continue;
                        }
                        if (tok == Sqlx.ROW)
                            Next();
                        if (or != null)
                            throw new DBException("42143", "OLD ROW").Mix();
                        if (tok == Sqlx.AS)
                            Next();
                        or = new Ident(this);
                        Mustbe(Sqlx.ID);
                    }
                    else
                    {
                        Mustbe(Sqlx.NEW);
                        if ((tgtype & PTrigger.TrigType.Delete) == PTrigger.TrigType.Delete)
                            throw new DBException("42146", "NEW", "DELETE").Mix();
                        if (tok == Sqlx.TABLE)
                        {
                            Next();
                            if (nt != null)
                                throw new DBException("42143", "NEW").Mix();
                            nt = new Ident(lxr.val.ToString(), tabl.iix);
                            Mustbe(Sqlx.ID);
                            continue;
                        }
                        if (tok == Sqlx.ROW)
                            Next();
                        if (nr != null)
                            throw new DBException("42143", "NEW ROW").Mix();
                        if (tok == Sqlx.AS)
                            Next();
                        nr = new Ident(lxr.val.ToString(), tabl.iix);
                        Mustbe(Sqlx.ID);
                    }
                }
            }
            if (tok == Sqlx.FOR)
            {
                Next();
                Mustbe(Sqlx.EACH);
                if (tok == Sqlx.ROW)
                {
                    Next();
                    tgtype |= PTrigger.TrigType.EachRow;
                }
                else
                {
                    Mustbe(Sqlx.STATEMENT);
                    if (tok == Sqlx.DEFERRED)
                    {
                        tgtype |= PTrigger.TrigType.Deferred;
                        Next();
                    }
                    tgtype |= PTrigger.TrigType.EachStatement;
                }
            }
            if ((tgtype & PTrigger.TrigType.EachRow) != PTrigger.TrigType.EachRow)
            {
                if (nr != null || or != null)
                    throw new DBException("42148").Mix();
            }
            var st = lxr.start;
            var cols = BList<long?>.Empty;
            for (var b=cls?.First(); b!=null; b=b.Next())
                if (cx.defs[b.value()] is Iix xi)
                    cols += xi.dp;
            var np = cx.db.nextPos;
            var pt = new PTrigger(trig.ident, tb.defpos, (int)tgtype, cols,
                    or, nr, ot, nt,
                    new Ident(new string(lxr.input, st, lxr.input.Length - st),
                        cx.Ix(st)),
                    nst, cx, np);
            var ix = LexPos();
            ParseTriggerDefinition(pt);
            pt.src = new Ident(new string(lxr.input, st, lxr.pos - st), ix);
            cx.parse = op;
            pt.framing = new Framing(cx, nst);
            cx.Add(pt);
            cx.result = -1L;
        }
        /// <summary>
        /// Event = 	INSERT | DELETE | (UPDATE [ OF id { ',' id } ] ) .
        /// </summary>
        /// <param name="type">ref: the trigger type</param>
		PTrigger.TrigType ParseTriggerHow(PTrigger.TrigType type)
		{
			if (tok==Sqlx.INSERT)
			{
				Next();
				type |= PTrigger.TrigType.Insert;
			} 
			else if (tok==Sqlx.UPDATE)
			{
				Next();
				type |= PTrigger.TrigType.Update;
			} 
			else
			{
				Mustbe(Sqlx.DELETE);
				type |= PTrigger.TrigType.Delete;
			}
            return type;
		}
        /// <summary>
		/// Alter =		ALTER DOMAIN id AlterDomain { ',' AlterDomain } 
		/// |	ALTER FUNCTION id '(' Parameters ')' RETURNS Type Statement
		/// |	ALTER PROCEDURE id '(' Parameters ')' Statement
		/// |	ALTER Method Statement
        /// |   ALTER TABLE id TO id
		/// |	ALTER TABLE id AlterTable { ',' AlterTable } 
        /// |	ALTER TYPE id AlterType { ',' AlterType } 
        /// |   ALTER VIEW id AlterView { ',' AlterView } 
        /// </summary>
        /// <returns>the Executable</returns>
		void ParseAlter()
		{
            if (cx.role.infos[cx.role.defpos]?.priv.HasFlag(Grant.Privilege.AdminRole)==false)
                throw new DBException("42105");
            Next();
            var op = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            MethodModes();
            Match(Sqlx.DOMAIN,Sqlx.TYPE,Sqlx.ROLE,Sqlx.VIEW);
			switch (tok)
			{
				case Sqlx.TABLE: ParseAlterTable(); break;
				case Sqlx.DOMAIN: ParseAlterDomain(); break; 
				case Sqlx.TYPE: ParseAlterType(); break;
				case Sqlx.FUNCTION: ParseAlterProcedure(); break;
				case Sqlx.PROCEDURE: ParseAlterProcedure(); break;
				case Sqlx.OVERRIDING: ParseMethodDefinition(); break;
				case Sqlx.INSTANCE: ParseMethodDefinition(); break;
				case Sqlx.STATIC: ParseMethodDefinition(); break;
				case Sqlx.CONSTRUCTOR: ParseMethodDefinition(); break;
				case Sqlx.METHOD: ParseMethodDefinition(); break;
                case Sqlx.VIEW: ParseAlterView(); break;
				default:
					throw new DBException("42125",tok).Mix();
			}
            cx.parse = op;
		}
        /// <summary>
        /// id AlterTable { ',' AlterTable } 
        /// </summary>
        /// <returns>the Executable</returns>
        void ParseAlterTable()
        {
            Next();
            Table tb;
            var o = new Ident(this);
            Mustbe(Sqlx.ID);
            tb = cx.GetObject(o.ident) as Table??
                throw new DBException("42107", o).Mix();
            var op = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            ParseAlterTableOps(tb);
            cx.parse = op;
        }
        /// <summary>
        /// AlterView = SET (INSERT|UPDATE|DELETE) SqlStatement
        ///     |   TO id
        ///     |   SET SOURCE To QueryExpression
        ///     |   [DROP]TableMetadata
        /// </summary>
        /// <returns>the executable</returns>
        void ParseAlterView()
        {
            Next();
            var nm = new Ident(this);
            Mustbe(Sqlx.ID);
            DBObject vw = cx.GetObject(nm.ident) ??
                throw new DBException("42107", nm).Mix();
            var op = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            ParseAlterOp(vw);
            while (tok == Sqlx.COMMA)
            {
                Next();
                ParseAlterOp(vw);
            }
            cx.parse = op;
        }
        /// <summary>
        /// Parse an alter operation
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="ob">the object to be affected</param>
        void ParseAlterOp(DBObject ob)
        {
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var kind = (ob is View)?Sqlx.VIEW:Sqlx.FUNCTION;
            long np;
            if (tok == Sqlx.SET && ob is View)
            {
                Next();
                int st;
                Ident s;
                var lp = LexPos();
                Mustbe(Sqlx.SOURCE);
                Mustbe(Sqlx.TO);
                st = lxr.start;
                RowSet qe;
                if (cx._Dom(ob) is not Domain qd)
                    throw new PEException("PE47133");
                qe = ParseQueryExpression(qd);
                s = new Ident(new string(lxr.input, st, lxr.start - st), lp);
                cx.Add(new Modify("Source", ob.defpos, qe, s, cx.db.nextPos, cx));
            }
            else if (tok == Sqlx.TO)
            {
                Next();
                var nm = lxr.val;
                Mustbe(Sqlx.ID);
                cx.Add(new Change(ob.defpos, nm.ToString(), cx.db.nextPos, cx));
            }
            else if (tok == Sqlx.ALTER)
            {
                Next();
                var ic = new Ident(this);
                Mustbe(Sqlx.ID);
                ob = cx.GetObject(ic.ident) ??
                    throw new DBException("42135", ic.ident);
                if (cx.role.Denied(cx, Grant.Privilege.Metadata))
                    throw new DBException("42105").Mix();
                var m = ParseMetadata(Sqlx.COLUMN);
                cx.Add(new PMetadata(ic.ident, 0, ob, m, cx.db.nextPos));
            }
            if (StartMetadata(kind) || Match(Sqlx.ADD))
            {
                if (cx.role.Denied(cx, Grant.Privilege.Metadata))
                    throw new DBException("42105").Mix();
                if (tok == Sqlx.ALTER)
                    Next();
                var m = ParseMetadata(kind);
                np = tr.nextPos;
                cx.Add(new PMetadata(ob.NameFor(cx), -1, ob,m,np));
            }
        }
        /// <summary>
        /// id AlterDomain { ',' AlterDomain } 
        /// </summary>
        /// <returns>the Executable</returns>
        void ParseAlterDomain()
        {
            Next();
            var c = ParseIdent();
            if (cx.GetObject(c.ident) is not Domain d)
                throw new DBException("42161", "domain id");
            ParseAlterDomainOp(d);
            while (tok == Sqlx.COMMA)
            {
                Next();
                ParseAlterDomainOp(d);
            }
        }
        /// <summary>
		/// AlterDomain =  SET DEFAULT TypedValue 
		/// |	DROP DEFAULT
		/// |	TYPE Type
		/// |	AlterCheck .
		/// AlterCheck =	ADD CheckConstraint 
		/// |	DROP CONSTRAINT id .
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="d">the domain object</param>
        void ParseAlterDomainOp(Domain d)
		{
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            if (tok == Sqlx.SET)
            {
                Next();
                Mustbe(Sqlx.DEFAULT);
                int st = lxr.start;
                var dv = ParseSqlValue(Domain.For(d.kind));
                string ds = new(lxr.input, st, lxr.start - st);
                cx.Add(new Edit(d, d.name, d + (Domain.Default, dv) + (Domain.DefaultString, ds),
                    cx.db.nextPos, cx));
            }
            else if (Match(Sqlx.ADD))
            {
                Next();
                Ident id;
                if (tok == Sqlx.CONSTRAINT)
                {
                    Next();
                    id = new Ident(this);
                    Mustbe(Sqlx.ID);
                }
                else
                    id = new Ident(this);
                Mustbe(Sqlx.CHECK);
                Mustbe(Sqlx.LPAREN);
                var nst = cx.db.nextStmt;
                int st = lxr.pos;
                var sc = ParseSqlValue(Domain.Bool).Reify(cx);
                string source = new(lxr.input, st, lxr.pos - st - 1);
                Mustbe(Sqlx.RPAREN);
                var pc = new PCheck(d, id.ident, sc, source, nst, tr.nextPos, cx);
                cx.Add(pc);
            }
            else if (tok == Sqlx.DROP)
            {
                Next();
                var dp = cx.db.types[d] ?? -1L;
                if (tok == Sqlx.DEFAULT)
                {
                    Next();
                    cx.Add(new Edit(d, d.name, d, tr.nextPos, cx));
                }
                else if (StartMetadata(Sqlx.DOMAIN) || Match(Sqlx.ADD, Sqlx.DROP))
                {
                    if (tr.role.Denied(cx, Grant.Privilege.Metadata))
                        throw new DBException("42105").Mix();
                    var m = ParseMetadata(Sqlx.DOMAIN);
                    cx.Add(new PMetadata(d.name, -1, d, m, dp));
                }
                else
                {
                    Mustbe(Sqlx.CONSTRAINT);
                    var n = new Ident(this);
                    Mustbe(Sqlx.ID);
                    Drop.DropAction s = ParseDropAction();
                    var ch = (Check?)cx.GetObject(n.ident) ?? throw new DBException("42135", n.ident);
                    cx.Add(new Drop1(ch.defpos, s, tr.nextPos));
                }
            }
            else if (StartMetadata(Sqlx.DOMAIN) || Match(Sqlx.ADD, Sqlx.DROP))
            {
                if (cx.role.Denied(cx, Grant.Privilege.Metadata))
                    throw new DBException("42105").Mix();
                cx.Add(new PMetadata(d.name, 0, d, ParseMetadata(Sqlx.DOMAIN),
                    tr.nextPos));
            }
            else
            {
                Mustbe(Sqlx.TYPE);
                var dt = ParseSqlDataType();
                cx.Add(new Edit(d, d.name, dt, tr.nextPos, cx));
            }
		}
        /// <summary>
        /// DropAction = | RESTRICT | CASCADE .
        /// </summary>
        /// <returns>RESTRICT (default) or CASCADE</returns>
		Drop.DropAction ParseDropAction()
		{
            Match(Sqlx.RESTRICT, Sqlx.CASCADE);
            Drop.DropAction r = 0;
			switch (tok)
			{
                case Sqlx.CASCADE: r = Drop.DropAction.Cascade; Next(); break;
                case Sqlx.RESTRICT: r = Drop.DropAction.Restrict; Next(); break;
            }
            return r;
		}
        /// <summary>
        /// |   ALTER TABLE id AlterTable { ',' AlterTable }
        /// </summary>
        /// <param name="tb">the database</param>
        /// <param name="tb">the table</param>
        void ParseAlterTableOps(Table tb)
		{
			ParseAlterTable(tb);
			while (tok==Sqlx.COMMA)
			{
				Next();
				ParseAlterTable(tb);
			}
		}
        /// <summary>
        /// AlterTable =   TO id
        /// |   Enforcement
        /// |   ADD ColumnDefinition
        ///	|	ALTER [COLUMN] id AlterColumn { ',' AlterColumn }
        /// |	DROP [COLUMN] id DropAction
        /// |	(ADD|DROP) (TableConstraintDef |VersioningClause)
        /// |   SET TableConstraintDef ReferentialAction
        /// |   ADD TablePeriodDefinition [ AddPeriodColumnList ]
        /// |   ALTER PERIOD id To id
        /// |   DROP TablePeriodDefinition
        /// |   [DROP] Metadata
        /// |	AlterCheck .
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="tb">the Table object</param>
        void ParseAlterTable(Table tb)
        {
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            cx.Add(tb.framing);
            if (tok == Sqlx.TO)
            {
                Next();
                var o = lxr.val;
                Mustbe(Sqlx.ID);
                cx.Add(new Change(tb.defpos, o.ToString(), tr.nextPos, cx));
                return;
            }
            if (tok == Sqlx.LEVEL)
            {
                ParseClassification(tb);
                return;
            }
            if (tok == Sqlx.SCOPE)
            {
                ParseEnforcement(tb);
                return;
            }
            Match(Sqlx.ADD);
            switch (tok)
            {
                case Sqlx.DROP:
                    {
                        Next();
                        switch (tok)
                        {
                            case Sqlx.CONSTRAINT:
                                {
                                    Next();
                                    var name = new Ident(this);
                                    Mustbe(Sqlx.ID);
                                    Drop.DropAction act = ParseDropAction();
                                    if (cx.GetObject(name.ident) is Check ck)
                                        cx.Add(new Drop1(ck.defpos, act, tr.nextPos));
                                    return;
                                }
                            case Sqlx.PRIMARY:
                                {
                                    Next();
                                    Mustbe(Sqlx.KEY);
                                    Drop.DropAction act = ParseDropAction();
                                    if (ParseColsList(tb) is not Domain cols)
                                        throw new DBException("42161", "cols");
                                    cols += (Domain.Kind, Sqlx.ROW);
                                    Level3.Index x = tb.FindIndex(cx.db, cols)?[0]
                                        ?? throw new DBException("42164", tb.NameFor(cx));
                                    if (x != null)
                                        cx.Add(new Drop1(x.defpos, act, tr.nextPos));
                                    return;
                                }
                            case Sqlx.FOREIGN:
                                {
                                    Next();
                                    Mustbe(Sqlx.KEY);
                                    if (ParseColsList(tb) is not Domain cols)
                                        throw new DBException("42161", "cols");
                                    Mustbe(Sqlx.REFERENCES);
                                    var n = new Ident(this);
                                    Mustbe(Sqlx.ID);
                                    var rt = cx.GetObject(n.ident) as Table ??
                                        throw new DBException("42107", n).Mix();
                                    var st = lxr.pos;
                                    if (tok == Sqlx.LPAREN && ParseColsList(rt) is null)
                                        throw new DBException("42161", "rcols");
                                    var x = tb.FindIndex(cx.db, cols)?[0];
                                    if (x != null)
                                    {
                                        cx.Add(new Drop(x.defpos, tr.nextPos));
                                        return;
                                    }
                                    throw new DBException("42135", new string(lxr.input, st, lxr.pos)).Mix();
                                }
                            case Sqlx.UNIQUE:
                                {
                                    Next();
                                    var st = lxr.pos;
                                    if (ParseColsList(tb) is not Domain cols)
                                        throw new DBException("42161", "cols");
                                    var x = tb.FindIndex(cx.db, cols)?[0];
                                    if (x != null)
                                    {
                                        cx.Add(new Drop(x.defpos, tr.nextPos));
                                        return;
                                    }
                                    throw new DBException("42135", new string(lxr.input, st, lxr.pos)).Mix();

                                }
                            case Sqlx.PERIOD:
                                {
                                    var ptd = ParseTablePeriodDefinition();
                                    var pd = (ptd.pkind == Sqlx.SYSTEM_TIME) ? tb.systemPS : tb.applicationPS;
                                    if (pd > 0)
                                        cx.Add(new Drop(pd, tr.nextPos));
                                    return;
                                }
                            case Sqlx.WITH:
                                ParseVersioningClause(tb, true); return;
                            default:
                                {
                                    if (StartMetadata(Sqlx.TABLE))
                                    {
                                        if (cx.role.Denied(cx, Grant.Privilege.Metadata))
                                            throw new DBException("42105").Mix();
                                        cx.Add(new PMetadata(tb.NameFor(cx), 0, tb,
                                                ParseMetadata(Sqlx.TABLE),
                                            tr.nextPos));
                                        return;
                                    }
                                    if (tok == Sqlx.COLUMN)
                                        Next();
                                    var name = new Ident(this);
                                    Mustbe(Sqlx.ID);
                                    Drop.DropAction act = ParseDropAction();
                                    var tc = (TableColumn?)tr.objects[cx._Dom(tb)?.ColFor(cx, name.ident) ?? -1L]
                                       ?? throw new DBException("42135", name);
                                    if (tc != null)
                                        cx.Add(new Drop1(tc.defpos, act, tr.nextPos));
                                    return;
                                }
                        }
                    }
                case Sqlx.ADD:
                    {
                        Next();
                        if (tok == Sqlx.PERIOD)
                            AddTablePeriodDefinition(tb);
                        else if (tok == Sqlx.WITH)
                            ParseVersioningClause(tb, false);
                        else if (tok == Sqlx.CONSTRAINT || tok == Sqlx.UNIQUE || tok == Sqlx.PRIMARY || tok == Sqlx.FOREIGN || tok == Sqlx.CHECK)
                            ParseTableConstraintDefin(tb);
                        else
                            ParseColumnDefin(tb);
                        break;
                    }
                case Sqlx.ALTER:
                    {
                        Next();
                        if (tok == Sqlx.COLUMN)
                            Next();
                        var o = new Ident(this);
                        Mustbe(Sqlx.ID);
                        var ft = cx._Dom(tb) ?? throw new DBException("42105");
                        var col = (TableColumn?)cx.db.objects[ft.ColFor(cx, o.ident)]
                                ?? throw new DBException("42112", o.ident);
                        while (StartMetadata(Sqlx.COLUMN) || Match(Sqlx.TO, Sqlx.POSITION, Sqlx.SET, Sqlx.DROP, Sqlx.ADD, Sqlx.TYPE))
                            col = ParseAlterColumn(tb, col, o.ident);
                        break;
                    }
                case Sqlx.PERIOD:
                    {
                        if (Match(Sqlx.ID))
                        {
                            var pid = lxr.val;
                            Next();
                            Mustbe(Sqlx.TO);
                            if (cx.db.objects[tb.applicationPS] is not PeriodDef pd)
                                throw new DBException("42162", pid).Mix();
                            pid = lxr.val;
                            Mustbe(Sqlx.ID);
                            cx.Add(new Change(pd.defpos, pid.ToString(), tr.nextPos, cx));
                        }
                        AddTablePeriodDefinition(tb);
                        break;
                    }
                case Sqlx.SET:
                    {
                        Next();
                        if (ParseColsList(tb) is not Domain cols)
                            throw new DBException("42161", "cols");
                        Mustbe(Sqlx.REFERENCES);
                        var n = new Ident(this);
                        Mustbe(Sqlx.ID);
                        var rt = cx.GetObject(n.ident) as Table ??
                            throw new DBException("42107", n).Mix();
                        var st = lxr.pos;
                        if (tok == Sqlx.LPAREN && ParseColsList(rt) is null)
                            throw new DBException("42161", "cols");
                        PIndex.ConstraintType ct = 0;
                        if (tok == Sqlx.ON)
                            ct = ParseReferentialAction();
                        cols += (Domain.Kind, Sqlx.ROW);
                        if (tb.FindIndex(cx.db, cols, PIndex.ConstraintType.ForeignKey)?[0] is Level3.Index x)
                        {
                            cx.Add(new RefAction(x.defpos, ct, tr.nextPos));
                            return;
                        }
                        throw new DBException("42135", new string(lxr.input, st, lxr.pos-st)).Mix();
                    }
                default:
                    if (StartMetadata(Sqlx.TABLE) || Match(Sqlx.ADD, Sqlx.DROP))
                        if (tb.Denied(cx, Grant.Privilege.Metadata))
                            throw new DBException("42105");
                    cx.Add(new PMetadata(tb.NameFor(cx), 0, tb, ParseMetadata(Sqlx.TABLE),
                        tr.nextPos));
                    break;
            }
        }    
        /// <summary>
        /// <summary>
		/// AlterColumn = 	TO id
        /// |   POSITION int
		/// |	(SET|DROP) ColumnConstraint 
		/// |	AlterDomain
        /// |	SET GenerationRule.
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="tb">the table object</param>
        /// <param name="tc">the table column object</param>
        TableColumn ParseAlterColumn(Table tb, TableColumn tc, string nm)
		{
            var tr = cx.db as Transaction?? throw new DBException("2F003");
			TypedValue o;
            if (tok == Sqlx.TO)
            {
                Next();
                var n = new Ident(this);
                Mustbe(Sqlx.ID);
                tc = (TableColumn)(cx.Add(new Change(tc.defpos, n.ident, tr.nextPos, cx))
                    ?? throw new DBException("42105"));
                return tc;
            }
            if (tok == Sqlx.POSITION)
            {
                Next();
                o = lxr.val;
                var cd = cx._Dom(tc)??throw new DBException("42105");
                Mustbe(Sqlx.INTEGERLITERAL);
                if (o.ToInt() is int n)
                    tc = (TableColumn)(cx.Add(new Alter3(tc.defpos, nm, n,tb,cd,
                        tc.generated.gfs ?? cd.defaultValue?.ToString() ?? "",
                        cd.defaultValue??TNull.Value,
                        "", tc.update, tc.notNull, tc.generated, tr.nextStmt, tr.nextPos, cx))
                        ??throw new DBException("42105"));
                return tc;
            }
            if (Match(Sqlx.ADD))
            {
                Next();
                while (StartMetadata(Sqlx.COLUMN))
                {
                    if (tb.Denied(cx, Grant.Privilege.Metadata))
                        throw new DBException("42105").Mix();
                    tc = (TableColumn)(cx.Add(new PMetadata(nm, 0, tc, ParseMetadata(Sqlx.COLUMN), tr.nextPos))
                        ?? throw new DBException("42105"));
                }
                if (tok == Sqlx.CONSTRAINT)
                    Next();
                var n = new Ident(this);
                Mustbe(Sqlx.ID);
                Mustbe(Sqlx.CHECK);
                Mustbe(Sqlx.LPAREN);
                int st = lxr.pos;
                var nst = cx.db.nextStmt;
                var se = ParseSqlValue(Domain.Bool).Reify(cx);
                string source = new(lxr.input, st, lxr.pos - st - 1);
                Mustbe(Sqlx.RPAREN);
                var pc = new PCheck(tc, n.ident, se, source, nst, tr.nextPos, cx);
                tc = (TableColumn)(cx.Add(pc) ?? throw new DBException("42105"));
                return tc;
            }
            if (tok == Sqlx.SET)
            {
                Next();
                if (tok == Sqlx.DEFAULT)
                {
                    Next();
                    int st = lxr.start;
                    var dv = lxr.val;
                    Next();
                    var ds = new string(lxr.input, st, lxr.start - st);
                    if (cx._Dom(tb) is not Domain td || cx._Dom(tc) is not Domain cd)
                        throw new PEException("PE47140");
                    tc = (TableColumn)(cx.Add(new Alter3(tc.defpos, nm, td.PosFor(cx, nm),
                        tb, cd, ds, dv, "",
                        CTree<UpdateAssignment, bool>.Empty, false, 
                        GenerationRule.None, tr.nextStmt, tr.nextPos, cx)) 
                        ?? throw new DBException("42105"));
                    return tc;
                }
                if (Match(Sqlx.GENERATED))
                {
                    Domain type = Domain.Row;
                    var oc = cx;
                    cx = cx.ForConstraintParse();
                    if (cx._Dom(tb) is not Domain td || cx._Dom(tc) is not Domain cd)
                        throw new PEException("PE47141");
                    var nst = cx.db.nextStmt;
                    var gr = ParseGenerationRule(tc.defpos,cd) + (DBObject._Framing, new Framing(cx,nst));
                    oc.DoneCompileParse(cx);
                    cx = oc;
                    if (td!=null && cd!=null)
                    {
                        tc.ColumnCheck(tr, true);
                        tc = (TableColumn)(cx.Add(new Alter3(tc.defpos, nm, td.PosFor(cx, nm), tb, cd, 
                            gr.gfs ?? type.defaultValue?.ToString() ?? "", type.defaultValue ?? TNull.Value, 
                            "", CTree<UpdateAssignment, bool>.Empty, tc.notNull, gr, nst, tr.nextPos, cx)) 
                            ?? throw new DBException("42105"));
                    }
                    return tc;
                }
                if (Match(Sqlx.NOT))
                {
                    Next();
                    Mustbe(Sqlx.NULL);
                    if (cx._Dom(tb) is not Domain td || cx._Dom(tc) is not Domain cd)
                        throw new PEException("PE47142");
                    if (td!=null && cd!=null)
                    {
                        tc.ColumnCheck(tr, false);
                        tc = (TableColumn)(cx.Add(new Alter3(tc.defpos, nm, td.PosFor(cx,nm),
                            tb,cd,"",TNull.Value, "", CTree<UpdateAssignment,bool>.Empty, 
                            true, tc.generated, tr.nextStmt, tr.nextPos, cx)) ?? throw new DBException("42105"));
                    }
                    return tc;
                }
                return ParseColumnConstraint(tb, tc);
            }
            else if (tok == Sqlx.DROP)
            {
                Next();
                if (StartMetadata(Sqlx.COLUMN))
                {
                    if (tb.Denied(cx, Grant.Privilege.Metadata))
                        throw new DBException("42105", tc.NameFor(cx)).Mix();
                    tc = (TableColumn)(cx.Add(new PMetadata(nm, 0, tc,
                                ParseMetadata(Sqlx.COLUMN), tr.nextPos)) ?? throw new DBException("42105"));
                }
                if (tok != Sqlx.DEFAULT && tok != Sqlx.NOT && tok != Sqlx.PRIMARY && tok != Sqlx.REFERENCES && tok != Sqlx.UNIQUE && tok != Sqlx.CONSTRAINT && !StartMetadata(Sqlx.COLUMN))
                    throw new DBException("42000", lxr.Diag).ISO();
                if (tok == Sqlx.DEFAULT)
                {
                    Next();
                    if (cx._Dom(tb) is not Domain td || cx._Dom(tc) is not Domain cd)
                        throw new PEException("PE47143");
                    tc = (TableColumn)(cx.Add(new Alter3(tc.defpos, nm, td.PosFor(cx, nm),
                        tb, cd, "", TNull.Value, tc.updateString ?? "", tc.update, tc.notNull,
                        GenerationRule.None, tr.nextStmt, tr.nextPos, cx)) ?? throw new DBException("42105"));
                    return tc;
                }
                else if (tok == Sqlx.NOT)
                {
                    Next();
                    Mustbe(Sqlx.NULL);
                    if (cx._Dom(tb) is not Domain td || cx._Dom(tc) is not Domain cd)
                        throw new PEException("PE47144");
                    if (td != null && cd != null)
                        tc = (TableColumn)(cx.Add(new Alter3(tc.defpos, nm, td.PosFor(cx, nm),
                            tb, cd, cd.defaultString, cd.defaultValue,
                            tc.updateString ?? "", tc.update, false,
                            tc.generated, tr.nextStmt, tr.nextPos, cx)) ?? throw new DBException("42105"));
                    return tc;
                }
                else if (tok == Sqlx.PRIMARY)
                {
                    Next();
                    Mustbe(Sqlx.KEY);
                    Drop.DropAction act = ParseDropAction();
                    if (tb.FindPrimaryIndex(cx) is Level3.Index x)
                    {
                        if (x.keys.Length != 1 || x.keys[0] != tc.defpos)
                            throw new DBException("42158", tb.NameFor(cx), tc.NameFor(cx)).Mix()
                                .Add(Sqlx.TABLE_NAME, new TChar(tb.NameFor(cx)))
                                .Add(Sqlx.COLUMN_NAME, new TChar(tc.NameFor(cx)));
                        cx.Add(new Drop1(x.defpos, act, tr.nextPos));
                    }
                    return tc;
                }
                else if (tok == Sqlx.REFERENCES)
                {
                    Next();
                    var n = new Ident(this);
                    Mustbe(Sqlx.ID);
                    if (tok == Sqlx.LPAREN)
                    {
                        Next();
                        Mustbe(Sqlx.ID);
                        Mustbe(Sqlx.RPAREN);
                    }
                    Level3.Index? dx = null;
                    for (var p = tb.indexes.First(); dx == null && p != null; p = p.Next())
                        for (var c = p.value().First(); dx == null && c != null; c = c.Next())
                            if (cx.db.objects[c.key()] is Level3.Index x && x.keys.Length == 1 && x.keys[0] == tc.defpos &&
                                cx.db.objects[x.reftabledefpos] is Table rt && rt.NameFor(cx) == n.ident)
                                dx = x;
                    if (dx == null)
                        throw new DBException("42159", nm, n.ident).Mix()
                            .Add(Sqlx.TABLE_NAME, new TChar(n.ident))
                            .Add(Sqlx.COLUMN_NAME, new TChar(nm));
                    else
                        cx.Add(new Drop(dx.defpos, tr.nextPos));
                    return tc;
                }
                else if (tok == Sqlx.UNIQUE)
                {
                    Next();
                    Level3.Index? dx = null;
                    for (var p = tb.indexes.First(); dx == null && p != null; p = p.Next())
                        for (var c = p.value().First(); dx == null && c != null; c = c.Next())
                            if (cx.db.objects[c.key()] is Level3.Index x && x.keys.Length == 1 &&
                                    x.keys[0] == tc.defpos &&
                                (x.flags & PIndex.ConstraintType.Unique) == PIndex.ConstraintType.Unique)
                                dx = x;
                    if (dx == null)
                        throw new DBException("42160", nm).Mix()
                            .Add(Sqlx.TABLE_NAME, new TChar(nm));
                    cx.Add(new Drop(dx.defpos, tr.nextPos));
                    return tc;
                }
                else if (tok == Sqlx.CONSTRAINT)
                {
                    var n = new Ident(this);
                    Mustbe(Sqlx.ID);
                    Drop.DropAction s = ParseDropAction();
                    var ch = cx.GetObject(n.ident) as Check ?? throw new DBException("42135", n.ident);
                    cx.Add(new Drop1(ch.defpos, s, tr.nextPos));
                    return tc;
                }
            }
            else if (Match(Sqlx.TYPE))
            {
                Next();
                Domain? type;
                if (tok == Sqlx.ID)
                {
                    var domain = new Ident(this);
                    Next();
                    type = cx._Dom(cx.GetObject(domain.ident));
                    if (type == null)
                        throw new DBException("42119", domain.ident, cx.db.name).Mix()
                            .Add(Sqlx.CATALOG_NAME, new TChar(cx.db.name))
                            .Add(Sqlx.TYPE, new TChar(domain.ident));
                }
                else if (Match(Sqlx.CHARACTER, Sqlx.CHAR, Sqlx.VARCHAR, Sqlx.NATIONAL, Sqlx.NCHAR,
                    Sqlx.BOOLEAN, Sqlx.NUMERIC, Sqlx.DECIMAL,
                    Sqlx.DEC, Sqlx.FLOAT, Sqlx.REAL, // Sqlx.LONG,Sqlx.DOUBLE,
                    Sqlx.INT, // Sqlx.BIGINT,
                    Sqlx.INTEGER,// Sqlx.SMALLINT,
                    Sqlx.BINARY, Sqlx.BLOB, Sqlx.NCLOB,
                    Sqlx.CLOB, Sqlx.DATE, Sqlx.TIME, Sqlx.ROW, Sqlx.TABLE)
                    && cx._Dom(tc) is Domain dm)
                {
                    type = ParseSqlDataType() + (Domain.Default, dm.defaultValue)
                        + (Domain.DefaultString, dm.defaultString);
                    type = (Domain)cx.Add(type);
                    if (cx._Dom(tc) is Domain cd && cx._Dom(tb) is Domain td)
                    {
                        if (!cd.CanTakeValueOf(type))
                            throw new DBException("2200G");
                        cx.Add(new Alter3(tc.defpos, nm, td.PosFor(cx, nm),
                            tb, type,
                            type.defaultString, type.defaultValue, tc.updateString??"", tc.update,
                            tc.notNull, tc.generated, tr.nextStmt, tr.nextPos, cx));
                    }
                    return tc;
                }
            }
            if (StartMetadata(Sqlx.COLUMN))
            {
                if (tb.Denied(cx, Grant.Privilege.Metadata))
                    throw new DBException("42105").Mix();
                var md = ParseMetadata(Sqlx.COLUMN);
                tc = (TableColumn)(cx.Add(new PMetadata(nm, 0, tc, md, tr.nextPos)) ?? throw new DBException("42105"));
            }
            return tc;
		}
        /// <summary>
		/// AlterType = TO id
        ///     |   ADD ( Member | Method )
		/// 	|	SET Member_id To id
        /// 	|   SET UNDER id
		/// 	|	DROP ( Member_id | (MethodType Method_id '('Type{','Type}')')) DropAction
        /// 	|   DROP UNDER id
		/// 	|	ALTER Member_id AlterMember { ',' AlterMember } .
        /// </summary>
        /// <returns>the executable</returns>
        void ParseAlterType()
        {
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            Next();
            var id = new Ident(this);
            Mustbe(Sqlx.ID);
            if (cx.role is not Role ro || (!ro.dbobjects.Contains(id.ident)) ||
            cx._Ob(ro.dbobjects[id.ident]??-1L) is not UDType tp || tp.infos[ro.defpos] is not ObInfo oi) 
                throw new DBException("42133", id.ident).Mix()
                    .Add(Sqlx.TYPE, new TChar(id.ident)); 
            if (tok == Sqlx.TO)
            {
                Next();
                id = new Ident(this);
                Mustbe(Sqlx.ID);
                cx.Add(new Change(ro.dbobjects[id.ident]??-1L, id.ident, tr.nextPos, cx));
            }
            else if (tok == Sqlx.SET)
            {
                Next();
                if (Match(Sqlx.UNDER))
                {
                    Next();
                    var ui = new Ident(this);
                    Mustbe(Sqlx.ID);
                    if (cx.role.dbobjects.Contains(ui.ident)
                        && cx.db.objects[cx.role.dbobjects[ui.ident]??-1L] is UDType tu)
                        cx.Add(new EditType(id, tp, tp, tu, cx.db.nextPos, cx));
                }
                else
                {
                    id = new Ident(this);
                    Mustbe(Sqlx.ID);
                    var sq = tp.PosFor(cx, id.ident);
                    var ts = tp.ColFor(cx, id.ident);
                    if (cx.db.objects[ts] is not TableColumn tc)
                        throw new DBException("42133", id).Mix()
                            .Add(Sqlx.TYPE, new TChar(id.ident));
                    Mustbe(Sqlx.TO);
                    id = new Ident(this);
                    Mustbe(Sqlx.ID);
                    if (cx._Dom(tc) is Domain cd)
                        new Alter3(tc.defpos, id.ident, sq,
                            (Table?)cx.db.objects[tc.tabledefpos] ?? throw new DBException("42105"),
                            cd, cd.defaultString,
                            cd.defaultValue, tc.updateString ?? "",
                            tc.update, tc.notNull, tc.generated, tr.nextStmt, tr.nextPos, cx);
                }
            }
            else if (tok == Sqlx.DROP)
            {
                Next();
                if (tok == Sqlx.UNDER)
                {
                    Next();
                    var st = cx._Ob(tp.structure) as Domain ?? throw new PEException("PE92612");
                    cx.Add(new EditType(id, tp, st, null, cx.db.nextPos, cx));
                }
                else
                {
                    id = new Ident(this);
                    if (MethodModes())
                    {
                        MethodName mn = ParseMethod(Domain.Null);
                        if (mn.name is not Ident nm || cx.db.objects[oi?.methodInfos?[nm.ident]?[cx.Signature(mn.ins)] ?? -1L] is not Method mt)
                            throw new DBException("42133", tp).Mix().
                                Add(Sqlx.TYPE, new TChar(tp.name));
                        ParseDropAction();
                        new Drop(mt.defpos, tr.nextPos);
                    }
                    else
                    {
                        if (cx.db.objects[tp.ColFor(cx, id.ident)] is not TableColumn tc)
                            throw new DBException("42133", id).Mix()
                                .Add(Sqlx.TYPE, new TChar(id.ident));
                        ParseDropAction();
                        new Drop(tc.defpos, tr.nextPos);
                    }
                }
            }
            else if (Match(Sqlx.ADD))
            {
                Next();
                MethodModes();
                if (Match(Sqlx.INSTANCE, Sqlx.STATIC, Sqlx.CONSTRUCTOR, Sqlx.OVERRIDING, Sqlx.METHOD))
                {
                    ParseMethodHeader(tp);
                    return;
                }
                var nc = tp.Length;
                var (nm,dm,md) = ParseMember(id);
                var tb = (Table?)cx.db.objects[tp.structure] ?? throw new PEException("PE1821");
                var c = new PColumn2(tb,nm.ident, nc, dm, dm.defaultString, dm.defaultValue,
                        false, GenerationRule.None, cx.db.nextStmt, tr.nextPos, cx);
                tb = (Table)(cx.Add(c) ?? throw new DBException("42105"));
                var tc = (TableColumn)(cx.obs[c.defpos]?? throw new DBException("42105"));
                if (md!=CTree<Sqlx,TypedValue>.Empty)
                    cx.Add(new PMetadata(nm.ident, 0, tc, md, tr.nextPos));
            }
            else if (tok == Sqlx.ALTER)
            {
                Next();
                id = new Ident(this);
                Mustbe(Sqlx.ID);
                if (cx.db.objects[tp.ColFor(cx, id.ident)] is not TableColumn tc)
                    throw new DBException("42133", id).Mix()
                        .Add(Sqlx.TYPE, new TChar(id.ident));
                ParseAlterMembers(tc);
            }
        }
        /// <summary>
        /// AlterMember =	TYPE Type
        /// 	|	SET DEFAULT TypedValue
        /// 	|	DROP DEFAULT .
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="tc">The UDType member</param>
        TableColumn ParseAlterMembers(TableColumn tc)
        {
            if (cx._Dom(tc) is not Domain cd || tc.infos[cx.role.defpos] is not ObInfo ci)
                throw new DBException("42105");
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            TypedValue dv = cd.defaultValue;
            var ds = "";
            for (; ; )
            {
                var nst = tr.nextStmt;
                if (tok == Sqlx.TO)
                {
                    Next();
                    var n = new Ident(this);
                    Mustbe(Sqlx.ID);
                    tc = (TableColumn)(cx.Add(new Change(tc.defpos, n.ident, tr.nextPos, cx))
                        ?? throw new DBException("42105"));
                    goto skip;
                }
                else if (Match(Sqlx.TYPE))
                {
                    Next();
                    tc += (DBObject._Domain, ParseSqlDataType().defpos);
                }
                else if (tok == Sqlx.SET)
                {
                    Next();
                    Mustbe(Sqlx.DEFAULT);
                    var st = lxr.start;
                    dv = lxr.val;
                    Next();
                    ds = new string(lxr.input, st, lxr.start - st);
                    tc += (Domain.DefaultString, ds);
                    tc += (Domain.Default, dv);
                }
                else if (tok == Sqlx.DROP)
                {
                    Next();
                    Mustbe(Sqlx.DEFAULT);
                    dv = TNull.Value;
                    tc += (Domain.Default, dv);
                }
                if (cx._Dom(tc.tabledefpos) is Domain td && ci.name!=null)
                    tc = (TableColumn)(cx.Add(new Alter3(tc.defpos, ci.name, td.PosFor(cx,ci.name), 
                         (Table?)cx.db.objects[tc.tabledefpos]??throw new DBException("42105"),
                         cd, ds, dv, tc.updateString??"", tc.update,
                         tc.notNull, GenerationRule.None, nst, tr.nextPos, cx))??throw new DBException("42105"));
            skip:
                if (tok != Sqlx.COMMA)
                    break;
                Next();
            }
            return tc;
        }
        /// <summary>
        /// FUNCTION id '(' Parameters ')' RETURNS Type Statement
        /// PROCEDURE id '(' Parameters ')' Statement
        /// </summary>
        /// <returns>the executable</returns>
		void ParseAlterProcedure()
        {
            bool func = tok == Sqlx.FUNCTION;
            Next();
            ParseProcedureClause(func, Sqlx.ALTER);
        }
        /// <summary>
		/// DropStatement = 	DROP DropObject DropAction .
		/// DropObject = 	ORDERING FOR id
		/// 	|	ROLE id
		/// 	|	TRIGGER id
		/// 	|	ObjectName .
        /// </summary>
        /// <returns>the executable</returns>
		void ParseDropStatement()
        {
            if (cx.role==null || cx.role.infos[cx.role.defpos]?.priv.HasFlag(Grant.Privilege.AdminRole)==false)
                throw new DBException("42105");
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            Next();
            if (Match(Sqlx.ORDERING))
            {
                Next(); Mustbe(Sqlx.FOR);
                var o = new Ident(this);
                Mustbe(Sqlx.ID);
                ParseDropAction(); // ignore if present
                var tp = cx.db.objects[cx.role.dbobjects[o.ident] ?? -1L] as Domain ??
                    throw new DBException("42133", o.ToString()).Mix();
                cx.Add(new Ordering(tp, -1L, OrderCategory.None, tr.nextPos, cx));
            }
            else
            {
                var (ob, _) = ParseObjectName();
                var a = ParseDropAction();
                ob.Cascade(cx, a);
            }
        }
        /// <summary>
		/// Type = 		StandardType | DefinedType | DomainName | REF(TableReference) .
		/// DefinedType = 	ROW  Representation
		/// 	|	TABLE Representation
        /// 	|   ( Type {, Type }) 
        ///     |   Type UNION Type { UNION Type }.
        /// </summary>
        /// <param name="pn">Parent ID (Type, or Procedure)</param>
		Domain ParseSqlDataType(Ident? pn=null)
        {
            Domain r;
            Sqlx tp = tok;
            if (Match(Sqlx.TABLE, Sqlx.ROW, Sqlx.TYPE, Sqlx.LPAREN))// anonymous row type
            {
                if (Match(Sqlx.TABLE, Sqlx.ROW, Sqlx.TYPE))
                    Next();
                else
                    tp = Sqlx.TYPE;
                if (tok == Sqlx.LPAREN)
                   return ParseRowTypeSpec(tp,pn); // pn is needed for tp==TYPE case
            }
            if (tok==Sqlx.ID && pn!=null && cx._Dom(cx.GetObject(pn.ident)) is UDType ut)
            {
                Next();
                ut.Defs(cx);
                return ut;
            }
            r = ParseStandardDataType();
            if (r == Domain.Null || r==Domain.Content)
            {
                var o = new Ident(this);
                Next();
                r = (Domain)(cx.db.objects[cx.role.dbobjects[o.ident]??-1L]??Domain.Content);
            } 
            if (tok == Sqlx.SENSITIVE)
            {
                r = (Domain)cx.Add(new Domain(cx.GetUid(),tok, r));
                Next();
            }
            return r;
        }
        /// <summary>
        /// StandardType = 	BooleanType | CharacterType | FloatType | IntegerType | LobType | NumericType | DateTimeType | IntervalType | XMLType .
        /// BooleanType = 	BOOLEAN .
        /// CharacterType = (([NATIONAL] CHARACTER) | CHAR | NCHAR | VARCHAR) [VARYING] ['('int ')'] [CHARACTER SET id ] Collate .
        /// Collate 	=	[ COLLATE id ] .
        /// There is no need to specify COLLATE UNICODE, since this is the default collation. COLLATE UCS_BASIC is supported but deprecated. For the list of available collations, see .NET documentation.
        /// FloatType =	(FLOAT|REAL) ['('int','int')'] .
        /// IntegerType = 	INT | INTEGER .
        /// LobType = 	BLOB | CLOB | NCLOB .
        /// CLOB is a synonym for CHAR in Pyrrho (both represent unbounded string). Similarly NCLOB is a synonym for NCHAR.
        /// NumericType = 	(NUMERIC|DECIMAL|DEC) ['('int','int')'] .
        /// DateTimeType =  (DATE | TIME | TIMESTAMP) ([IntervalField [ TO IntervalField ]] | ['(' int ')']).
        /// The use of IntervalFields when declaring DateTimeType  is an addition to the SQL standard.
        /// XMLType =	XML .
        /// </summary>
        /// <returns>the obs type</returns>
        Domain ParseStandardDataType()
        {
            Domain r = Domain.Null;
            Domain r0 = Domain.Null;
            if (Match(Sqlx.CHARACTER, Sqlx.CHAR, Sqlx.VARCHAR))
            {
                r = r0 = Domain.Char;
                Next();
                if (tok == Sqlx.LARGE)
                {
                    Next();
                    Mustbe(Sqlx.OBJECT); // CLOB is CHAR in Pyrrho
                }
                else if (tok == Sqlx.VARYING)
                    Next();
                r = ParsePrecPart(r);
                if (tok == Sqlx.CHARACTER)
                {
                    Next();
                    Mustbe(Sqlx.SET);
                    var o = new Ident(this);
                    Mustbe(Sqlx.ID);
                    r += (Domain.Charset, (Common.CharSet)Enum.Parse(typeof(Common.CharSet), o.ident, false));
                }
                if (tok == Sqlx.COLLATE)
                    r += (Domain.Culture, new CultureInfo(ParseCollate()));
            }
            else if (Match(Sqlx.NATIONAL, Sqlx.NCHAR))
            {
                if (tok == Sqlx.NATIONAL)
                {
                    Next();
                    Mustbe(Sqlx.CHARACTER);
                }
                else
                    Next();
                r = r0 = Domain.Char;
                if (tok == Sqlx.LARGE)
                {
                    Next();
                    Mustbe(Sqlx.OBJECT); // NCLOB is NCHAR in Pyrrho
                }
                r = ParsePrecPart(r);
            }
            else if (Match(Sqlx.NUMERIC, Sqlx.DECIMAL, Sqlx.DEC))
            {
                r = r0 = Domain.Numeric;
                Next();
                r = ParsePrecScale(r);
            }
            else if (Match(Sqlx.FLOAT, Sqlx.REAL, Sqlx.DOUBLE))
            {
                r = r0 = Domain.Real;
                if (tok == Sqlx.DOUBLE)
                    Mustbe(Sqlx.PRECISION);
                Next();
                r = ParsePrecPart(r);
            }
            else if (Match(Sqlx.INT, Sqlx.INTEGER, Sqlx.BIGINT, Sqlx.SMALLINT))
            {
                r = r0 = Domain.Int;
                Next();
                r = ParsePrecPart(r);
            }
            else if (Match(Sqlx.BINARY))
            {
                Next();
                Mustbe(Sqlx.LARGE);
                Mustbe(Sqlx.OBJECT);
                r = r0 = Domain.Blob;
            }
            else if (Match(Sqlx.BOOLEAN))
            {
                r = r0 = Domain.Bool;
                Next();
            }
            else if (Match(Sqlx.CLOB, Sqlx.NCLOB))
            {
                r = r0 = Domain.Char;
                Next();
            }
            else if (Match(Sqlx.BLOB, Sqlx.XML))
            {
                r = r0 = Domain.Blob;
                Next();
            }
            else if (Match(Sqlx.DATE, Sqlx.TIME, Sqlx.TIMESTAMP, Sqlx.INTERVAL))
            {
                Domain dr = r0 = Domain.Timestamp;
                switch (tok)
                {
                    case Sqlx.DATE: dr = Domain.Date; break;
                    case Sqlx.TIME: dr = Domain.Timespan; break;
                    case Sqlx.TIMESTAMP: dr = Domain.Timestamp; break;
                    case Sqlx.INTERVAL: dr = Domain.Interval; break;
                }
                Next();
                if (Match(Sqlx.YEAR, Sqlx.DAY, Sqlx.MONTH, Sqlx.HOUR, Sqlx.MINUTE, Sqlx.SECOND))
                    dr = ParseIntervalType();
                r = dr;
            }
            else if (Match(Sqlx.PASSWORD))
            {
                r = r0 = Domain.Password;
                Next();
            }
            else if (Match(Sqlx.POSITION))
            {
                r = r0 = Domain.Position;
                Next();
            }
            else if (Match(Sqlx.DOCUMENT))
            {
                r = r0 = Domain.Document;
                Next();
            }
            else if (Match(Sqlx.DOCARRAY))
            {
                r = r0 = Domain.DocArray;
                Next();
            }
            else if (Match(Sqlx.CHECK))
            {
                r = r0 = Domain.Rvv;
                Next();
            }
            else if (Match(Sqlx.OBJECT))
            {
                r = r0 = Domain.ObjectId;
                Next();
            }
            if (r == Domain.Null)
                return Domain.Null; // not a standard type
            if (r == r0)
                return r0; // completely standard
            // see if we know this type
            if (cx.db.objects[cx.db.types[r]??-1L] is Domain nr)
                return (Domain)cx.Add(nr);
            if (cx.newTypes.Contains(r) && cx.obs[cx.newTypes[r]??-1L] is Domain ns)
                return (Domain)cx.Add(ns);
            r = (Domain)r.New(cx,r.mem);
            var pp = new PDomain(r, cx.db.nextPos, cx);
            cx.Add(pp);
            return (Domain)cx.Add(pp.domain);
        }
        /// <summary>
		/// IntervalType = 	INTERVAL IntervalField [ TO IntervalField ] .
		/// IntervalField = 	YEAR | MONTH | DAY | HOUR | MINUTE | SECOND ['(' int ')'] .
        /// </summary>
        /// <param name="q">The Domain being specified</param>
        /// <returns>the modified obs type</returns>
        Domain ParseIntervalType()
		{
			Sqlx start = Mustbe(Sqlx.YEAR,Sqlx.DAY,Sqlx.MONTH,Sqlx.HOUR,Sqlx.MINUTE,Sqlx.SECOND);
            var d = Domain.Interval;
            var m = d.mem+(Domain.Start, start);
			if (tok==Sqlx.LPAREN)
			{
				Next();
				var p1 = lxr.val;
				Mustbe(Sqlx.INTEGERLITERAL);
				m+=(Domain.Scale,p1.ToInt()??0);
				if (start==Sqlx.SECOND && tok==Sqlx.COMMA)
				{
					Next();
					var p2 = lxr.val;
					Mustbe(Sqlx.INTEGERLITERAL);
					m+=(Domain.Precision,p2.ToInt()??0);
				}
				Mustbe(Sqlx.RPAREN);
			}
			if (tok==Sqlx.TO)
			{
				Next();
				Sqlx end = Mustbe(Sqlx.YEAR,Sqlx.DAY,Sqlx.MONTH,Sqlx.HOUR,Sqlx.MINUTE,Sqlx.SECOND);
                m += (Domain.End, end);
				if (end==Sqlx.SECOND && tok==Sqlx.LPAREN)
				{
					Next();
					var p2 = lxr.val;
					Mustbe(Sqlx.INTEGERLITERAL);
					m+=(Domain.Precision,p2.ToInt()??0);
					Mustbe(Sqlx.RPAREN);
				}
			}
            return (Domain)d.New(cx,m);
		}
        /// <summary>
        /// Handle ROW type or TABLE type in Type specification.
        /// If we are not already parsing a view definition (cx.parse==Compile), we need to
        /// construct a new PTable for the user defined type.
        /// </summary>
        /// <param name="d">The type of domain (TYPE, ROW or TABLE)</param>
        /// <param name="tdp">ref: the typedefpos</param>
        /// <returns>The RowTypeSpec</returns>
        internal Domain ParseRowTypeSpec(Sqlx k, Ident? pn = null, Domain? under = null)
        {
            var dt = under?? k switch
            {
                Sqlx.NODETYPE => Domain.NodeType,
                Sqlx.EDGETYPE => Domain.EdgeType,
                Sqlx.ROW => Domain.Row,
                Sqlx.TYPE => Domain.TypeSpec,
                Sqlx.VIEW => Domain.TableType,
                Sqlx.TABLE => Domain.TableType,
                _ => Domain.Null
            };
            if (tok == Sqlx.ID)
            {
                var id = new Ident(this);
                Next();
                if (cx.GetObject(id.ident) is not DBObject ob || cx._Dom(ob) is not Domain dm)
                    throw new DBException("42107", id.ident).Mix();
                return dm;
            }
            var lp = LexPos();
            var ns = BList<(Ident, Domain, CTree<Sqlx, TypedValue>)>.Empty;
            var sl = lxr.start;
            var nst = cx.db.nextStmt;
            if (k != Sqlx.ROW && k != Sqlx.VIEW)
                pn ??= new Ident("", lp);
            var m = dt.mem - Domain.RowType - Domain.Representation;
            Mustbe(Sqlx.LPAREN);
            for (var n = 0; ; n++)
            {
                ns += ParseMember(pn);
                if (tok != Sqlx.COMMA)
                    break;
                Next();
            }
            Mustbe(Sqlx.RPAREN);
            var ic = new Ident(new string(lxr.input, sl, lxr.start - sl), lp);
            var st = -1L;
            var dp = cx.GetUid();
            Table? t = null;
            string tn = (pn!=null)?(pn.ident+":"):ic.ident;
            if (k != Sqlx.VIEW)
            {
                st = cx.db.nextPos;
                t = (Table)(cx.Add(new PTable(tn, (Domain)(dt.New(lp.dp, m)), dp, st, cx))
                    ?? throw new DBException("42105"));
            }
            else if (pn == null && cx.parse != ExecuteStatus.Parse)
            {
                t = new VirtualTable(ic, cx, new Domain(dp, cx, Sqlx.VIEW, BList<DBObject>.Empty), nst);
                cx.Add(t);
                st = t.defpos;
            }
            var ms = CTree<long, Domain>.Empty;
            var rt = BList<long?>.Empty;
            var j = 0;
            for (var b = ns.First(); b != null; b = b.Next(), j++)
            {
                var (nm, dm, _) = b.value();
                if (k == Sqlx.TYPE && pn != null && t!=null)
                {
                    var np = cx.db.nextPos;
                    var pc = new PColumn3(t, nm.ident, j, dm,
                        "", dm.defaultValue, "", CTree<UpdateAssignment, bool>.Empty,
                        false, GenerationRule.None, cx.db.nextStmt, np, cx);
                    cx.Add(pc);
                    ms += (pc.defpos, dm);
                    rt += pc.defpos;
                    var cix = cx.Ix(pc.defpos);
                    cx.defs += (new Ident(pn, nm), cix);
                }
                else if (pn != null)
                {
                    var se = new SqlElement(nm, pn, dm);
                    cx.Add(se);
                    ms += (se.defpos, dm);
                    rt += se.defpos;
                    cx.defs += (new Ident(pn, nm), pn.iix);
                }
                else // RestView
                {
                    var sv = new SqlValue(nm, dm,
                        new BTree<long, object>(DBObject._From, st));
                    cx.Add(sv);
                    ms += (sv.defpos, dm);
                    rt += sv.defpos;
                    var cix = cx.Ix(sv.defpos);
                    cx.defs += (nm, cix);
                }
            }
            var r = (Domain)(dt.New(dp, BTree<long, object>.Empty
                + (ObInfo.Name, tn) + (Domain.Kind,k)
                + (Domain.Representation, ms) + (Domain.RowType, rt)
                + (Domain.Structure, st)));
            if (under !=null)
                r += (UDType.Under, under);
            cx.Add(r);
            if (t != null)
            {
                t += (DBObject._Framing, new Framing(cx, st));
                cx.Add(t);
                cx.Add(t.framing);
            }
            else
                cx.Add(new Framing(cx, nst));
            return r;
        }
        /// <summary>
        /// Member = id Type [DEFAULT TypedValue] Collate .
        /// </summary>
        /// <returns>The RowTypeColumn</returns>
		(Ident,Domain,CTree<Sqlx,TypedValue>) ParseMember(Ident? pn)
		{
            Ident? n = null;
            if (tok == Sqlx.ID)
            {
                n = new Ident(this);
                Next();
            }
            if (ParseSqlDataType(pn) is not Domain dm)
                throw new DBException("42161", "type");
            if (tok == Sqlx.ID && n == null)
                throw new DBException("42000",dm);
			if (tok==Sqlx.DEFAULT)
			{
				int st = lxr.start;
				var dv = ParseSqlValue(dm);
                var ds = new string(lxr.input, st, lxr.start - st);
				dm = dm + (Domain.Default,dv) + (Domain.DefaultString,ds);
			}
            if (tok == Sqlx.COLLATE)
                dm+= (Domain.Culture,ParseCollate());
            var md = CTree<Sqlx, TypedValue>.Empty;
            if (StartMetadata(Sqlx.COLUMN))
                md = ParseMetadata(Sqlx.COLUMN);
            if (n == null || dm == null || md == null)
                throw new DBException("42000");
            return (n,dm,md);
		}
        /// <summary>
        /// Parse a precision
        /// </summary>
        /// <param name="r">The SqldataType</param>
        /// <returns>the updated obs type</returns>
		Domain ParsePrecPart(Domain r)
		{
			if (tok==Sqlx.LPAREN)
			{
				Next();
                if (lxr.val is TInt it)
                {
                    int prec = (int)it.value;
                    r += (Domain.Precision, prec);
                }
                Mustbe(Sqlx.INTEGERLITERAL);
				Mustbe(Sqlx.RPAREN);
			}
            return r;
		}
        /// <summary>
        /// Parse a precision and scale
        /// </summary>
        /// <param name="r">The sqldatatype</param>
        /// <returns>the updated obs type</returns>
		Domain ParsePrecScale(Domain r)
		{
			if (tok==Sqlx.LPAREN)
			{
				Next();
                if (lxr.val is TInt it && it is TInt i)
                {
                    int prec = (int)i.value;
                    r += (Domain.Precision, prec);
                }
				Mustbe(Sqlx.INTEGERLITERAL);
				if (tok==Sqlx.COMMA)
				{
					Next();
                    if (lxr.val is TInt jt && jt is TInt j)
                    {
                        int scale = (int)j.value;
                        r+=(Domain.Scale,scale);
                    }
					Mustbe(Sqlx.INTEGERLITERAL);
				}
				Mustbe(Sqlx.RPAREN);
			}
            return r;
		}
        /// <summary>
        /// Rename =SET ObjectName TO id .
        /// </summary>
        /// <returns>the executable</returns>
		void ParseSqlSet()
        {
            Next();
            if (Match(Sqlx.AUTHORIZATION))
            {
                Next();
                Mustbe(Sqlx.EQL);
                Mustbe(Sqlx.CURATED);
                if (cx.db is not Transaction)throw new DBException("2F003");
                if (cx.db.user == null || cx.db.user.defpos != cx.db.owner)
                    throw new DBException("42105").Mix();
                if (cx.parse == ExecuteStatus.Obey)
                    cx.Add(new Curated(cx.db.nextPos));
            }
            else if (Match(Sqlx.PROFILING))
            {
                Next();
                Mustbe(Sqlx.EQL);
                if (cx.db.user == null || cx.db.user.defpos != cx.db.owner)
                    throw new DBException("42105").Mix();
                Mustbe(Sqlx.BOOLEANLITERAL);
                // ignore for now
            }
            else if (Match(Sqlx.TIMEOUT))
            {
                Next();
                Mustbe(Sqlx.EQL);
                if (cx.db.user == null || cx.db.user.defpos != cx.db.owner)
                    throw new DBException("42105").Mix();
                Mustbe(Sqlx.INTEGERLITERAL);
                // ignore for now
            }
            else
            {
                // Rename
                Ident? n;
                Match(Sqlx.DOMAIN, Sqlx.ROLE, Sqlx.VIEW, Sqlx.TYPE);
                MethodModes();
                DBObject? ob;
                if (Match(Sqlx.TABLE, Sqlx.DOMAIN, Sqlx.ROLE, Sqlx.VIEW, Sqlx.TYPE))
                {
                    Next();
                    n = new Ident(this);
                    Mustbe(Sqlx.ID);
                    ob = (Role?)cx.db.objects[cx.db.roles[n.ident]??-1L];
                }
                else
                {
                    bool meth = false;
                    PMethod.MethodType mt = PMethod.MethodType.Instance;
                    if (Match(Sqlx.OVERRIDING, Sqlx.STATIC, Sqlx.INSTANCE, Sqlx.CONSTRUCTOR))
                    {
                        switch (tok)
                        {
                            case Sqlx.OVERRIDING: mt = PMethod.MethodType.Overriding; break;
                            case Sqlx.STATIC: mt = PMethod.MethodType.Static; break;
                            case Sqlx.CONSTRUCTOR: mt = PMethod.MethodType.Constructor; break;
                        }
                        Next();
                        Mustbe(Sqlx.METHOD);
                        meth = true;
                    }
                    else if (tok == Sqlx.METHOD)
                        meth = true;
                    else if (!Match(Sqlx.PROCEDURE, Sqlx.FUNCTION))
                        throw new DBException("42126").Mix();
                    Next();
                    n = new Ident(this);
                    var nid = n.ident;
                    Mustbe(Sqlx.ID);
                    var a = CList<Domain>.Empty;
                    if (tok == Sqlx.LPAREN)
                    {
                        Next();
                        a += ParseSqlDataType();
                        while (tok == Sqlx.COMMA)
                        {
                            Next();
                            a += ParseSqlDataType();
                        }
                        Mustbe(Sqlx.RPAREN);
                    }
                    if (meth)
                    {
                        Ident? type = null;
                        if (mt == PMethod.MethodType.Constructor)
                            type = new Ident(nid, cx.Ix(0));
                        if (tok == Sqlx.FOR)
                        {
                            Next();
                            type = new Ident(this);
                            Mustbe(Sqlx.ID);
                        }
                        if (type == null)
                            throw new DBException("42134").Mix();
                        if (cx.role is not Role ro || 
                            cx.GetObject(type.ident) is not DBObject ot || 
                            ot.infos[ro.defpos] is not ObInfo oi)
                            throw new DBException("42105"); ;
                        ob = (Method?)cx.db.objects[oi.methodInfos[n.ident]?[a] ?? -1L];
                    }
                    else
                        ob = cx.GetProcedure(LexPos().dp, n.ident, a);
                    if (ob == null)
                        throw new DBException("42135", n.ident).Mix();
                    Mustbe(Sqlx.TO);
                    var nm = new Ident(this);
                    Mustbe(Sqlx.ID);
                    if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                        cx.Add(new Change(ob.defpos, nm.ident, tr.nextPos, cx));
                }
            }
        }
        /// <summary>
		/// CursorSpecification = [ XMLOption ] QueryExpression  .
        /// </summary>
        /// <param name="xp">The result expected (default Domain.Content)</param>
        /// <returns>A CursorSpecification</returns>
		internal SelectStatement ParseCursorSpecification(Domain xp, THttpDate? st = null,
            Rvv? rv = null,bool ambient=false)
        {
            RowSet un = _ParseCursorSpecification(xp,ambient);
            var s = new SelectStatement(cx.GetUid(), un);
            cx.exec = s;
            return (SelectStatement)cx.Add(s);
        }
        internal RowSet _ParseCursorSpecification(Domain xp,bool ambient=false)
        {
            if (!ambient)
                cx.IncSD(new Ident(this));
            ParseXmlOption(false);
            RowSet qe;
            qe = ParseQueryExpression(xp,ambient);
            cx.result = qe.defpos;
            cx.Add(qe);
            if (!ambient)
               cx.DecSD();
            return qe;
        }
        /// <summary>
        /// Start the parse for a QueryExpression (called from View)
        /// </summary>
        /// <param name="sql">The sql string</param>
        /// <param name="xp">The expected result type</param>
        /// <returns>a RowSet</returns>
		public RowSet ParseQueryExpression(Ident sql,Domain xp)
		{
			lxr = new Lexer(cx,sql);
			tok = lxr.tok;
            if (LexDp() > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
			return ParseQueryExpression(xp);
		}
        /// <summary>
        /// QueryExpression = QueryExpressionBody [OrderByClause] [FetchFirstClause] .
		/// QueryExpressionBody = QueryTerm 
		/// | QueryExpressionBody ( UNION | EXCEPT ) [ ALL | DISTINCT ] QueryTerm .
		/// A simple table query is defined (SQL2003-02 14.1SR18c) as a CursorSpecification 
        /// in which the RowSetExpr is a QueryTerm that is a QueryPrimary that is a QuerySpecification.
        /// </summary>
        /// <param name="xp">the expected result type</param>
        /// <returns>Updated result type, and a RowSet</returns>
		RowSet ParseQueryExpression(Domain xp,bool ambient=false)
        {
            RowSet left,right;
            left = ParseQueryTerm(xp,ambient);
            while (Match(Sqlx.UNION, Sqlx.EXCEPT))
            {
                Sqlx op = tok;
                Next();
                Sqlx md = Sqlx.DISTINCT;
                if (Match(Sqlx.ALL, Sqlx.DISTINCT))
                {
                    md = tok;
                    Next();
                }
                right = ParseQueryTerm(xp,ambient);
                left = new MergeRowSet(cx.GetUid(), cx, xp,left, right,md==Sqlx.DISTINCT,op);
                if (md == Sqlx.DISTINCT)
                    left += (RowSet.Distinct, true);
            }
            var ois = left.ordSpec;
            var nis = ParseOrderClause(ois, true);
            if (ois.CompareTo(nis)!=0)
                left = left.Sort(cx, nis, false);
            if (Match(Sqlx.FETCH))
            {
                Next();
                Mustbe(Sqlx.FIRST);
                var o = lxr.val;
                var n = 1;
                if (tok == Sqlx.INTEGERLITERAL)
                {
                    n = o.ToInt()??1;
                    Next();
                    Mustbe(Sqlx.ROWS);
                }
                else
                    Mustbe(Sqlx.ROW);
                left = new RowSetSection(cx, left, 0, n);
                Mustbe(Sqlx.ONLY);
            }
            return (RowSet)cx.Add(left);
        }
        /// <summary>
		/// QueryTerm = QueryPrimary | QueryTerm INTERSECT [ ALL | DISTINCT ] QueryPrimary .
        /// A simple table query is defined (SQL2003-02 14.1SR18c) as a CursorSpecification 
        /// in which the QueryExpression is a QueryTerm that is a QueryPrimary that is a QuerySpecification.
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <returns>the RowSet</returns>
		RowSet ParseQueryTerm(Domain xp,bool ambient = false)
		{
            RowSet left,right;
            left = ParseQueryPrimary(xp,ambient);
			while (Match(Sqlx.INTERSECT))
			{
                var lp = LexPos();
				Next();
				Sqlx m = Sqlx.DISTINCT;
				if (Match(Sqlx.ALL,Sqlx.DISTINCT))
				{
					m = tok;
					Next();
				}
                right = ParseQueryPrimary(xp,ambient);
				left = new MergeRowSet(lp.dp, cx, xp, left,right,m==Sqlx.DISTINCT,Sqlx.INTERSECT);
                if (m == Sqlx.DISTINCT)
                    left += (RowSet.Distinct, true);
			}
			return (RowSet)cx.Add(left);
		}
        /// <summary>
		/// QueryPrimary = QuerySpecification |  TypedValue | TABLE id .
        /// A simple table query is defined (SQL2003-02 14.1SR18c) as a CursorSpecification in which the 
        /// QueryExpression is a QueryTerm that is a QueryPrimary that is a QuerySpecification.
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <returns>the updated result type and the RowSet</returns>
		RowSet ParseQueryPrimary(Domain xp, bool ambient = false)
		{
            var lp = LexPos();
            RowSet qs;
            switch (tok)
            {
                case Sqlx.LPAREN:
                    Next();
                    qs = ParseQueryExpression(xp,ambient);
                    Mustbe(Sqlx.RPAREN);
                    break;
                case Sqlx.SELECT: // query specification
                    {
                        qs = ParseQuerySpecification(xp,ambient);
                        break;
                    }
                case Sqlx.VALUES:
                    var v = BList<long?>.Empty;
                    Sqlx sep = Sqlx.COMMA;
                    while (sep == Sqlx.COMMA)
                    {
                        Next();
                        var llp = LexPos();
                        Mustbe(Sqlx.LPAREN);
                        var x = ParseSqlValueList(xp);
                        Mustbe(Sqlx.RPAREN);
                        v += cx.Add(new SqlRow(llp.dp, cx, xp, x)).defpos;
                        sep = tok;
                    }
                    qs = (RowSet)cx.Add(new SqlRowSet(lp.dp, cx, xp, v));
                    break;
                case Sqlx.TABLE:
                    Next();
                    Ident ic = new(this);
                    Mustbe(Sqlx.ID);
                    var tb = cx.GetObject(ic.ident) as Table ??
                        throw new DBException("42107", ic.ident);
                    var td = cx._Dom(tb) ?? throw new PEException("PE47160");
                    qs = tb.RowSets(ic, cx, td, ic.iix.dp, Grant.Privilege.Select);
                    break;
                default:
                    throw new DBException("42127").Mix();
            }
            return (RowSet)cx.Add(qs);
		}
        /// <summary>
		/// OrderByClause = ORDER BY BList<long?> { ',' BList<long?> } .
        /// </summary>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the list of OrderItems</returns>
		Domain ParseOrderClause(Domain ord,bool wfok)
		{
			if (tok!=Sqlx.ORDER)
				return ord;
            cx.IncSD(new Ident(this)); // order by columns will be in the foregoing cursor spec
			Next();
			Mustbe(Sqlx.BY);
            var bs = BList<DBObject>.Empty;
            for (var b = ord.rowType.First(); b != null; b = b.Next())
                bs += cx._Ob(b.value() ?? -1L) ?? SqlNull.Value;
			bs+=cx._Ob(ParseOrderItem(wfok))??SqlNull.Value;
			while (tok==Sqlx.COMMA)
			{
				Next();
                bs += cx._Ob(ParseOrderItem(wfok)) ?? SqlNull.Value;
			}
            cx.DecSD();
            return new Domain(cx.GetUid(),cx,Sqlx.ROW,bs,bs.Length);
		}
        /// <summary>
        /// This version is for WindowSpecifications
        /// </summary>
        /// <param name="ord"></param>
        /// <returns></returns>
        Domain ParseOrderClause(Domain ord)
        {
            if (tok != Sqlx.ORDER)
                return ord;
            Next();
            Mustbe(Sqlx.BY);
            var bs = BList<DBObject>.Empty;
            for (var b = ord.rowType.First(); b != null; b = b.Next())
                bs += cx._Ob(b.value() ?? -1L) ?? SqlNull.Value;
            bs += cx._Ob(ParseOrderItem(false)) ?? SqlNull.Value;
            while (tok == Sqlx.COMMA)
            {
                Next();
                bs += cx._Ob(ParseOrderItem(false)) ?? SqlNull.Value;
            }
            return new Domain(cx.GetUid(), cx, Sqlx.ROW, bs, bs.Length);
        }
        /// <summary>
		/// BList<long?> =  TypedValue [ ASC | DESC ] [ NULLS ( FIRST | LAST )] .
        /// </summary>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>an OrderItem</returns>
		long ParseOrderItem(bool wfok)
		{
            var v = ParseSqlValue(Domain.Content,wfok);
            var dt = cx._Dom(v)??Domain.Null;
            var a = Sqlx.ASC;
            var n = Sqlx.NULL;
            if (Match(Sqlx.ASC))
				Next();
			else if (Match(Sqlx.DESC))
			{
				a = Sqlx.DESC;
				Next();
			}
			if (Match(Sqlx.NULLS))
			{
				Next();
				if (Match(Sqlx.FIRST))
					Next();
				else if (tok==Sqlx.LAST)
				{
					n = Sqlx.LAST;
					Next();
				}
			}
            if (a == dt.AscDesc && n == dt.nulls)
                return v.defpos;
            if (dt.defpos < Transaction.Analysing)
                dt = (Domain)dt.Relocate(cx.GetUid());
            dt += (Domain.Descending,a);
            dt += (Domain.NullsFirst,n);
            cx.Add(dt);
			return cx.Add(new SqlTreatExpr(cx.GetUid(),v,dt)).defpos;
		}
        /// <summary>
		/// RowSetSpec = SELECT [ALL|DISTINCT] SelectList [INTO Targets] TableExpression .
        /// Many identifiers in the selectList will be resolved in the TableExpression.
        /// This select list and tableExpression may both contain queries.
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <returns>The RowSetSpec</returns>
		RowSet ParseQuerySpecification(Domain xp, bool ambient = false)
        {
            var id = new Ident(this);
            if (!ambient)
                cx.IncSD(id);
            Mustbe(Sqlx.SELECT);
            var d = ParseDistinctClause();
            var dm = ParseSelectList(id.iix.dp, xp);
            cx.Add(dm);
            var te = ParseTableExpression(id.iix, dm, xp);
            if (Match(Sqlx.FOR))
            {
                Next();
                Mustbe(Sqlx.UPDATE);
            }
            if (!ambient)
                cx.DecSD(dm,te);
            te = (RowSet?)cx.obs[te.defpos]??throw new PEException("PE1967");
            if (d)
                te = new DistinctRowSet(cx, te);
            return te;
        }
        /// <summary>
        /// [DISTINCT|ALL]
        /// </summary>
        /// <returns>whether DISTINCT has been specified</returns>
		bool ParseDistinctClause()
		{
			bool r = false;
			if (tok==Sqlx.DISTINCT)
			{
				Next();
				r = true;
			} 
			else if (tok==Sqlx.ALL)
				Next();
			return r;
		}
        /// <summary>
		/// SelectList = '*' | SelectItem { ',' SelectItem } .
        /// </summary>
        /// <param name="dp">The position of the SELECT keyword</param>
        /// <param name="xp">the expected result type, or Domain.Content</param>
		Domain ParseSelectList(long dp, Domain xp)
        {
            SqlValue v;
            var j = 0;
            var vs = BList<DBObject>.Empty;
            v = ParseSelectItem(dp, xp, j++);
            if (v!=null) // star items do not have a value to add at this stage
                vs += v;
            while (tok == Sqlx.COMMA)
            {
                Next();
                v = ParseSelectItem(dp, xp, j++);
                if (v!=null)
                    vs += v;
            }
            return (Domain)cx.Add(new Domain(cx.GetUid(), cx, Sqlx.TABLE, vs, vs.Length));
        }
        SqlValue ParseSelectItem(long q,Domain xp,int pos)
        {
            Domain dm = Domain.Content;
            if (xp.rowType.Length>pos)
                dm = xp.representation[xp[pos]??-1L]??throw new PEException("PE1675");
            return ParseSelectItem(q,dm);
        }
        /// <summary>
		/// SelectItem = * | (Scalar [AS id ]) | (RowValue [.*] [AS IdList]) .
        /// </summary>
        /// <param name="q">the query being parsed</param>
        /// <param name="t">the expected obs type for the query</param>
        /// <param name="pos">The position in the SelectList</param>
        SqlValue ParseSelectItem(long q,Domain xp)
        {
            Ident alias;
            SqlValue v;
            if (tok == Sqlx.TIMES)
            {
                var lp = LexPos();
                Next();
                v = new SqlStar(lp.dp, -1L);
            }
            else
            {
                v = ParseSqlValue(xp, true);
                v = v.AddFrom(cx, q);
            }
            if (tok == Sqlx.AS)
            {
                Next();
                alias = new Ident(this);
                var n = v.name;
                var nv = v;
                if (n == "")
                    nv += (ObInfo.Name, alias.ident);
                else
                    nv += (DBObject._Alias, alias.ident);
                if (cx.defs.Contains(alias.ident) && cx.defs[alias.ident]?[alias.iix.sd].Item1 is Iix ob
                    && cx.obs[ob.dp] is SqlValue ov)
                {
                    var v0 = nv;
                    nv = (SqlValue)nv.Relocate(ov.defpos);
                    cx.Replace(v0, nv);
                }
                else
                    cx.Add(nv);
                cx.defs += (alias, new Iix(v.defpos, cx, v.defpos));
                cx.Add(nv);
                Mustbe(Sqlx.ID);
                v = nv;
            }
            else
                cx.Add(v);
            var dm = cx._Dom(v);
            if (dm?.kind==Sqlx.TABLE)
            {
                // we want a scalar from this
                v += (DBObject._Domain, cx.obs[dm.rowType[0]??-1L]?.domain??Domain.Content.defpos);
                cx.Add(v);
            }
            return v;
        }
        /// <summary>
		/// TableExpression = FromClause [ WhereClause ] [ GroupByClause ] [ HavingClause ] [WindowClause] .
        /// The ParseFromClause is called before this
        /// </summary>
        /// <param name="q">the query</param>
        /// <param name="t">the expected obs type</param>
        /// <returns>The TableExpression</returns>
		RowSet ParseTableExpression(Iix lp, Domain dm, Domain xp)
        {
            RowSet fm = ParseFromClause(lp.lp);
            var m = fm.mem;
            for (var b=fm.SourceProps.First();b!=null;b=b.Next())
                if (b.value() is long p)
                    m -= p;
            m += (RowSet._Source, fm.defpos);
            var vs = BList<DBObject>.Empty;
            for (var b = dm.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx._Ob(p) is SqlValue sv)
                {
                    (var ls, m) = sv.Resolve(cx, lp.dp, m);
                    vs += ls;
                }
            for (var b = cx.undefined.First(); b != null; b = b.Next())
            {
                var k = b.key();
                var ob = cx.obs[k];
                if (ob is SqlValue sv)
                    sv.Resolve(cx, k, BTree<long, object>.Empty);
                else if (ob?.id is Ident ic && ob is ForwardReference fr
                    && cx.defs[ic.ident] is BTree<int,(Iix,Ident.Idents)> tt
                    && tt.Contains(cx.sD))
                {
                    var (iix, _) = tt[cx.sD];
                    if (cx.obs[iix.dp] is RowSet nb)
                    {
                        cx.Replace(ob, nb);
                        if (nb.alias!=null)
                            cx.UpdateDefs(ic, nb, nb.alias);
                        cx.undefined -= k;
                    }
                    for (var c = fr.subs.First(); c != null; c = c.Next())
                        if (cx.obs[c.key()] is DBObject os && os.id != null)
                        {
                            var (iiy, _) = cx.defs[(os.id.ident,cx.sD)];
                            if (cx.obs[iiy.dp] is DBObject oy)
                            {
                                cx.Replace(os, oy);
                                cx.undefined -= c.key();
                            }
                        }
                }
            }
            fm = (RowSet)(cx.obs[fm.defpos]??throw new PEException("PE1666"));
            var df = cx._Dom(fm) ?? Domain.Null;
            var ds = vs.Length;
            for (var b = df.rowType.First(); b != null; b = b.Next())
                if (cx._Ob(b.key()) is SqlValue v && v.defpos >= Transaction.HeapStart) // ??
                        vs += v;
            dm = new Domain(dm.defpos, cx, Sqlx.TABLE, vs,ds);
            cx.Add(dm);
            m += (DBObject._Domain, dm.defpos);
            fm = (RowSet)(cx.obs[fm.defpos]??throw new PEException("PE2001"));
            RowSet r = new SelectRowSet(lp, cx, dm, fm, m);
            m = BTree<long, object>.Empty;
            if (dm.aggs != CTree<long, bool>.Empty) 
                m += (Domain.Aggs, dm.aggs);
            if (tok == Sqlx.WHERE)
            {
                var wc = ParseWhereClause() ?? throw new DBException("42161", "condition");
                var wh = new BTree<long,object>(RowSet._Where, wc);
                m += wh;
                ((RowSet)(cx.obs[fm.defpos]??throw new PEException("PE2002"))).Apply(wh,cx);
            }
            if (tok == Sqlx.GROUP)
            {
                if (dm.aggs == CTree<long, bool>.Empty)
                    throw new DBException("42128", "GROUP");
                m += (RowSet.Group, ParseGroupClause()?.defpos ?? -1L);
            }
            if (tok == Sqlx.HAVING)
            {
                if (dm.aggs == CTree<long, bool>.Empty)
                    throw new DBException("42128", "HAVING");
                m += (RowSet.Having, ParseHavingClause(dm));
            }
            if (tok == Sqlx.WINDOW)
            {
                if (dm.aggs == CTree<long, bool>.Empty)
                    throw new DBException("42128", "WINDOW");
                m += (RowSet.Windows, ParseWindowClause());
            }
            r = r.Apply(m, cx);
            var dr = cx._Dom(r) ?? Domain.Null;
            if (dr.aggs.Count > 0)
            {
                var vw = true;
                for (var b = dr.aggs.First(); b != null; b = b.Next())
                    if ((b.key() >= Transaction.TransPos && b.key() < Transaction.Executables)
                        || b.key() >= Transaction.HeapStart)
                        vw = false;
                if (!vw)
                {
                    // check for agged or grouped
                    var os = CTree<long, bool>.Empty;
                    for (var b = dr.rowType.First(); b != null && b.key() < dr.display; b = b.Next())
                        if (b.value() is long p && cx.obs[p] is SqlValue x)
                            os += x.Operands(cx);
                    for (var b = r.having.First(); b != null; b = b.Next())
                        if (cx.obs[b.key()] is SqlValue x)
                        os += x.Operands(cx);
                    for (var b = os.First(); b != null; b = b.Next())
                        if (cx.obs[b.key()] is SqlValue v && !v.AggedOrGrouped(cx, r))
                            throw new DBException("42170", v.alias ?? v.name??"??");
                }
            }
            return r;
        }
        /// <summary>
		/// FromClause = 	FROM TableReference { ',' TableReference } .
        /// (before WHERE, GROUP, etc).
        /// </summary>
        /// <param name="dp">The position for the selectrowset being constructed</param>
        /// <param name="dm">the selectlist </param>
        /// <returns>The resolved select domain and table expression</returns>
		RowSet ParseFromClause(long dp)
		{
            if (tok == Sqlx.FROM)
            {
                Next();
                return (RowSet)cx.Add(ParseTableReference(dp));
            }
            else
                return new TrivialRowSet(cx);
		}
        /// <summary>
		/// TableReference = TableFactor Alias | JoinedTable .
        /// </summary>
        /// <param name="st">the future selectrowset defining position</param>
        /// <returns>and the new table reference item</returns>
        RowSet ParseTableReference(long st)
        {
            RowSet a;
            a = ParseTableReferenceItem(st);
            cx.Add(a);
            var lp = LexPos();
            while (Match(Sqlx.COMMA,Sqlx.CROSS, Sqlx.NATURAL, Sqlx.JOIN, Sqlx.INNER, Sqlx.LEFT, Sqlx.RIGHT, Sqlx.FULL))
                a = ParseJoinPart(lp.dp, a.Apply(new BTree<long,object>(DBObject._From,lp.dp),cx));
            return a;
        }
        /// <summary>
		/// TableFactor = 	Table_id [TimePeriodSpecification]
		/// | 	View_id 
		/// | 	Table_FunctionCall 
        /// |   Subquery
        /// |   ROWS '(' int [',' int] ')'
		/// | 	'(' TableReference ')'
		/// | 	TABLE '('  Value ')' 
		/// | 	UNNEST '('  Value ')'  (should allow a comma separated list of array values)
        /// |   STATIC
        /// |   '[' docs ']' .
        /// Subquery = '(' QueryExpression ')' .
        /// </summary>
        /// <param name="st">the defining position of the selectrowset being constructed</param>
        /// <returns>the rowset for this table reference</returns>
		RowSet ParseTableReferenceItem(long st)
        {
            RowSet rf;
            var lp = new Iix(st,cx,LexPos().dp);
            if (tok == Sqlx.ROWS) // Pyrrho specific
            {
                Next();
                Mustbe(Sqlx.LPAREN);
                var v = ParseSqlValue(Domain.Position);
                SqlValue w = SqlNull.Value;
                if (tok == Sqlx.COMMA)
                {
                    Next();
                    w = ParseSqlValue(Domain.Position);
                }
                Mustbe(Sqlx.RPAREN);
                if (tok == Sqlx.ID || tok == Sqlx.AS)
                {
                    if (tok == Sqlx.AS)
                        Next();
                    new Ident(this);
                    Mustbe(Sqlx.ID);
                }
                RowSet rs;
                if (w != SqlNull.Value)
                    rs = new LogRowColRowSet(lp.dp, cx,
                        Domain.Int.Coerce(cx, v.Eval(cx)).ToLong() ?? -1L,
                        Domain.Int.Coerce(cx, w.Eval(cx)).ToLong() ?? -1L);
                else
                    rs = new LogRowsRowSet(lp.dp, cx,
                        Domain.Int.Coerce(cx, v.Eval(cx)).ToLong() ?? -1L);
                cx.Add(rs);
                rf = rs;
            }
            // this syntax should allow multiple array/multiset arguments and ORDINALITY
            else if (tok == Sqlx.UNNEST)
            {
                Next();
                Mustbe(Sqlx.LPAREN);
                SqlValue sv = ParseSqlValue(Domain.Content);
                cx.Add(sv);
                var dm = cx._Dom(sv) ?? Domain.Null;
                if (dm.elType.kind != Sqlx.ROW)
                    throw new DBException("42161", dm);
                if (dm.kind == Sqlx.ARRAY)
                    rf = new ArrayRowSet(cx.GetUid(), cx, sv);
                else if (dm.kind == Sqlx.MULTISET)
                    rf = new MultisetRowSet(cx.GetUid(), cx, sv);
                else throw new DBException("42161", sv);
                Mustbe(Sqlx.RPAREN);
            }
            else if (tok == Sqlx.TABLE)
            {
                Next();
                var cp = LexPos();
                Mustbe(Sqlx.LPAREN); // SQL2003-2 7.6 required before table valued function
                Ident n = new(this);
                Mustbe(Sqlx.ID);
                var r = BList<long?>.Empty;
                Mustbe(Sqlx.LPAREN);
                if (tok != Sqlx.RPAREN)
                    for (; ; )
                    {
                        r += cx.Add(ParseSqlValue(Domain.Content)).defpos;
                        if (tok == Sqlx.RPAREN)
                            break;
                        Mustbe(Sqlx.COMMA);
                    }
                Next();
                Mustbe(Sqlx.RPAREN); // another: see above
                var proc = cx.GetProcedure(LexPos().dp, n.ident, cx.Signature(r))
                    ?? throw new DBException("42108", n.ident);
                var pd = cx._Dom(proc) ?? Domain.Null;
                ParseCorrelation(pd);
                var cs = new CallStatement(n.iix.dp, cx, proc, proc.NameFor(cx), r);
                cx.Add(cs);
                var ca = new SqlProcedureCall(cp.dp, cs);
                cx.Add(ca);
                rf = ca.RowSets(n, cx, pd, n.iix.dp);
            }
            else if (tok == Sqlx.LPAREN) // subquery
            {
                Next();
                rf = ParseQueryExpression(Domain.TableType);
                Mustbe(Sqlx.RPAREN);
                if (tok == Sqlx.ID && cx._Dom(rf) is Domain dr)
                {
                    var a = lxr.val.ToString();
                    var rx = cx.Ix(rf.defpos);
                    var ia = new Ident(a, rx);
                    for (var b = cx.defs[a]?.Last()?.value().Item2.First(); b != null; b = b.Next())
                        if (cx.obs[b.value()?.Last()?.value().Item1.dp ?? -1L] is SqlValue lv
                            && (lv.domain == Domain.Content.defpos || lv.GetType().Name == "SqlValue")
                            && lv.name != null
                            && rf.names.Contains(lv.name) && cx.obs[rf.names[lv.name]??-1L] is SqlValue uv)
                        {
                            var nv = uv.Relocate(lv.defpos);
                            cx.Replace(lv, nv);
                            cx.Replace(uv, nv);
                        }
                    cx.defs += (ia, rx);
                    cx.AddDefs(ia, dr);
                    Next();
                }
            }
            else if (tok == Sqlx.STATIC)
            {
                Next();
                rf = new TrivialRowSet(cx);
            }
            else if (tok == Sqlx.LBRACK)
                rf = new TrivialRowSet(cx) + (RowSet.Target, ParseSqlDocArray().defpos);
            else // ordinary table, view, OLD/NEW TABLE id, or parameter
            {
                Ident ic = new(this);
                Mustbe(Sqlx.ID);
                string? a = null;
                if (tok == Sqlx.ID || tok == Sqlx.AS)
                {
                    if (tok == Sqlx.AS)
                        Next();
                    a = lxr.val.ToString();
                    Mustbe(Sqlx.ID);
                }
                var ob = (cx.GetObject(ic.ident) ?? cx.obs[cx.defs[ic].dp]) ??
                    throw new DBException("42107", ic.ToString());
                if (cx._Dom(ob) is not Domain od)
                    throw new PEException("PE47163");
                if (ob is SqlValue o && (od.kind != Sqlx.TABLE || o.from < 0))
                    throw new DBException("42000");
                if (ob is RowSet f)
                {
                    rf = f;
                    ob = cx.obs[f.target] as Table;
                }
                else
                    rf = _From(ic, ob, od, st, Grant.Privilege.Select, a);
                if (Match(Sqlx.FOR))
                {
                    var ps = ParsePeriodSpec();
                    var tb = ob as Table ?? throw new DBException("42000");
                    rf += (RowSet.Periods, rf.periods + (tb.defpos, ps));
                    long pp = (ps.periodname == "SYSTEM_TIME") ? tb.systemPS : tb.applicationPS;
                    if (pp < 0)
                        throw new DBException("42162", ps.periodname).Mix();
                    rf += (RowSet.Periods, rf.periods + (tb.defpos, ps));
                }
                var rx = cx.Ix(rf.defpos);
                if (cx.dbformat < 51)
                    cx.defs += (new Ident(rf.defpos.ToString(), rx), rx);
            }
            return rf; 
        }
        /// <summary>
        /// We are about to call the From constructor, which may
        /// Resolve undefined expressions in the SelectList 
        /// </summary>
        /// <param name="dp">The occurrence of this table reference</param>
        /// <param name="ob">The table or view referenced</param>
        /// <param name="q">The expected result for the enclosing query</param>
        /// <returns></returns>
        RowSet _From(Ident ic, DBObject ob, Domain q,long st, Grant.Privilege pr, string? a=null)
        {
            var dp = ic.iix.dp;
            if (ob != null)
            {
                if (ob is View ov)
                    ob = ov.Instance(dp, cx); 
                ob._Add(cx);
            }
            if (ob == null)
                throw new PEException("PE2003");
            var ff = ob.RowSets(ic, cx, q, ic.iix.dp, pr, a);
            return ff;
        }
        /// <summary>
        /// TimePeriodSpec = 
        ///    |    AS OF TypedValue
        ///    |    BETWEEN(ASYMMETRIC|SYMMETRIC)  TypedValue AND TypedValue
        ///    |    FROM TypedValue TO TypedValue .
        /// </summary>
        /// <returns>The periodSpec</returns>
        PeriodSpec ParsePeriodSpec()
        {
            string pn = "SYSTEM_TIME";
            Sqlx kn;
            SqlValue? t1 = null, t2 = null;
            Next();
            if (tok == Sqlx.ID)
                pn = lxr.val.ToString();
            Mustbe(Sqlx.SYSTEM_TIME,Sqlx.ID);
            kn = tok;
            switch (tok)
            {
                case Sqlx.AS: Next();
                    Mustbe(Sqlx.OF);
                    t1 = ParseSqlValue(Domain.UnionDate);
                    break;
                case Sqlx.BETWEEN: Next();
                    kn = Sqlx.ASYMMETRIC;
                    if (Match(Sqlx.ASYMMETRIC))
                        Next();
                    else if (Match(Sqlx.SYMMETRIC))
                    {
                        Next();
                        kn = Sqlx.SYMMETRIC;
                    }
                    t1 = ParseSqlValueTerm(Domain.UnionDate, false);
                    Mustbe(Sqlx.AND);
                    t2 = ParseSqlValue(Domain.UnionDate);
                    break;
                case Sqlx.FROM: Next();
                    t1 = ParseSqlValue(Domain.UnionDate);
                    Mustbe(Sqlx.TO);
                    t2 = ParseSqlValue(Domain.UnionDate);
                    break;
                default:
                    kn  =Sqlx.NO;
                    break;
            }
            return new PeriodSpec(pn, kn, t1, t2);
        }
        /// <summary>
        /// Alias = 		[[AS] id [ Cols ]] .
        /// Creates a new ObInfo for the derived table.
        /// </summary>
        /// <returns>The correlation info</returns>
		ObInfo? ParseCorrelation(Domain xp)
		{
            if (tok == Sqlx.ID || tok == Sqlx.AS)
			{
				if (tok==Sqlx.AS)
					Next();
                var cs = BList<long?>.Empty;
                var rs = CTree<long, Domain>.Empty;
                var tablealias = new Ident(this);
				Mustbe(Sqlx.ID);
				if (tok==Sqlx.LPAREN)
				{
					Next();
                    var ids = ParseIDList();
                    if (ids.Length != xp.Length)
                        throw new DBException("22000",xp);
                    var ib = ids.First();
                    for (var b = xp.rowType.First(); ib != null && b != null; b = b.Next(), ib = ib.Next())
                        if (b.value() is long oc)
                        {
                            var cp = ib.value().iix.dp;
                            var cd = xp.representation[oc] ?? throw new PEException("PE47169");
                            cs += cp;
                            rs += (cp, cd);
                        }
                    xp = new Domain(cx.GetUid(),cx, Sqlx.TABLE, rs, cs);
                    cx.Add(xp);
                    return new ObInfo(tablealias.ident, Grant.Privilege.Execute);
				} else
                    return new ObInfo(tablealias.ident, Grant.Privilege.Execute);
			}
            return null;
		}
        /// <summary>
		/// JoinType = 	INNER | ( LEFT | RIGHT | FULL ) [OUTER] .
        /// </summary>
        /// <param name="v">The JoinPart being parsed</param>
		Sqlx ParseJoinType()
		{
            Sqlx r = Sqlx.INNER;
			if (tok==Sqlx.INNER)
				Next();
			else if (tok==Sqlx.LEFT||tok==Sqlx.RIGHT||tok==Sqlx.FULL)
			{
                r = tok;
                Next();
			}
			if (r!=Sqlx.INNER && tok==Sqlx.OUTER)
				Next();
            return r;
		}
        /// <summary>
		/// JoinedTable = 	TableReference CROSS JOIN TableFactor 
		/// 	|	TableReference NATURAL [JoinType] JOIN TableFactor
		/// 	|	TableReference [JoinType] JOIN TableReference ON SqlValue .
        /// </summary>
        /// <param name="q">The eexpected domain q</param>
        /// <param name="fi">The RowSet so far</param>
        /// <returns>the updated query</returns>
        RowSet ParseJoinPart(long dp, RowSet fi)
        {
            var left = fi;
            Sqlx jkind;
            RowSet right;
            var m = BTree<long, object>.Empty;
            if (Match(Sqlx.COMMA))
            {
                jkind = Sqlx.CROSS;
                Next();
                right = ParseTableReferenceItem(dp);
            }
            else if (Match(Sqlx.CROSS))
            {
                jkind = Sqlx.CROSS;
                Next();
                Mustbe(Sqlx.JOIN);
                right = ParseTableReferenceItem(dp);
            }
            else if (Match(Sqlx.NATURAL))
            {
                m += (JoinRowSet.Natural, tok);
                Next();
                jkind = ParseJoinType();
                Mustbe(Sqlx.JOIN);
                right = ParseTableReferenceItem(dp);
            }
            else
            {
                jkind = ParseJoinType();
                Mustbe(Sqlx.JOIN);
                right = ParseTableReferenceItem(dp);
                if (tok == Sqlx.USING)
                {
                    m += (JoinRowSet.Natural, tok);
                    Next();
                    var ns = ParseIDList();
                    var sd = cx.sD;
                    var (_, li) = (left.alias!=null)?cx.defs[(left.alias,sd)] : cx.defs[(left.name,sd)];
                    var (_, ri) = (right.alias!=null)? cx.defs[(right.alias,sd)] : cx.defs[(right.name,sd)];
                    var cs = BTree<long, long?>.Empty;
                    for (var b = ns.First(); b != null; b = b.Next())
                        cs += (ri[b.value()].dp, li[b.value()].dp);
                    m += (JoinRowSet.JoinUsing, cs);
                }
                else
                {
                    Mustbe(Sqlx.ON);
                    var oc = ParseSqlValue(Domain.Bool).Disjoin(cx);
                    var on = BTree<long, long?>.Empty;
                    var wh = CTree<long, bool>.Empty;
                    left = (RowSet)(cx.obs[left.defpos]??throw new PEException("PE2005"));
                    right = (RowSet)(cx.obs[right.defpos]??throw new PEException("PE2006"));
                    var ls = CList<SqlValue>.Empty;
                    var rs = CList<SqlValue>.Empty;
                    var dl = cx._Dom(left) ?? Domain.Null;
                    var dr = cx._Dom(right)?? Domain.Null;
                    var lm = cx.Map(dl.rowType);
                    var rm = cx.Map(dr.rowType);
                    for (var b = oc.First(); b != null; b = b.Next())
                    { 
                        if (cx.obs[b.key()] is not SqlValueExpr se || cx._Dom(se) is not Domain de ||
                                de.kind != Sqlx.BOOLEAN)
                            throw new DBException("42151");
                        var lf = se.left;
                        var rg = se.right;
                        if (cx.obs[lf] is SqlValue sl && cx.obs[rg] is SqlValue sr && se.kind == Sqlx.EQL)
                        {
                            var rev = !lm.Contains(lf);
                            if (rev)
                            {
                                if ((!rm.Contains(lf))
                                    || (!lm.Contains(rg)))
                                    throw new DBException("42151");
                                oc += (cx.Add(new SqlValueExpr(se.defpos, cx, Sqlx.EQL,
                                    sr, sl, Sqlx.NO)).defpos, true);
                                ls += sr;
                                rs += sl;
                                on += (rg, lf);
                            }
                            else
                            {
                                if (!rm.Contains(rg))
                                    throw new DBException("42151");
                                ls += sl;
                                rs += sr;
                                on += (lf, rg);
                            }
                        }
                        else
                        {
                            oc -= se.defpos;
                            wh += (se.defpos, true);
                        }
                    }
                    if (oc!=CTree<long,bool>.Empty)
                        m += (JoinRowSet.JoinCond, oc);
                    if (on != BTree<long, long?>.Empty)
                        m += (JoinRowSet.OnCond, on);
                    if (wh != CTree<long,bool>.Empty)
                        m += (RowSet._Where, wh);
                }
            }
            var r = new JoinRowSet(dp, cx, left, jkind, right, m);
            return (JoinRowSet)cx.Add(r);
        }
        /// <summary>
		/// GroupByClause = GROUP BY [DISTINCT|ALL] GroupingElement { ',' GroupingElement } .
        /// GroupingElement = GroupingSet | (ROLLUP|CUBE) '('GroupingSet {',' GroupingSet} ')'  
        ///     | GroupSetsSpec | '('')' .
        /// GroupingSet = Col | '(' Col {',' Col } ')' .
        /// GroupingSetsSpec = GROUPING SETS '(' GroupingElement { ',' GroupingElement } ')' .
        /// </summary>
        /// <returns>The GroupSpecification</returns>
        GroupSpecification? ParseGroupClause()
        {
            if (tok != Sqlx.GROUP)
                return null;
            Next();
            var lp = LexPos();
            Mustbe(Sqlx.BY);
            bool d = false;
            if (tok == Sqlx.ALL)
                Next();
            else if (tok == Sqlx.DISTINCT)
            {
                Next();
                d = true;
            }
            bool simple = true;
            GroupSpecification r = new(lp.dp,cx,BTree<long, object>.Empty
                + (GroupSpecification.DistinctGp, d));
            r = ParseGroupingElement(r,ref simple);
            while (tok == Sqlx.COMMA)
            {
                Next();
                r = ParseGroupingElement(r,ref simple);
            }
            // simplify: see SQL2003-02 7.9 SR 10 .
            if (simple && r.sets.Count > 1)
            {
                var ms = CTree<long, int>.Empty;
                var i = 0;
                for (var g = r.sets.First(); g != null; g = g.Next())
                    if (g.value() is long gp)
                        for (var h = ((Grouping?)cx.obs[gp])?.members.First(); h != null; h = h.Next())
                            ms += (h.key(), i++);
                var gn = new Grouping(cx, new BTree<long, object>(Grouping.Members, ms));
                cx.Add(gn);
                r += (GroupSpecification.Sets, new BList<long?>(gn.defpos));
            }
            return (GroupSpecification)cx.Add(r);
        }
        /// <summary>
        /// A grouping element
        /// </summary>
        /// <param name="g">the group specification</param>
        /// <param name="simple">whether it is simple</param>
        /// <returns>whether it is simple</returns>
        GroupSpecification ParseGroupingElement(GroupSpecification g, ref bool simple)
        {
             if (Match(Sqlx.ID))
            {
                var cn = ParseIdent();
                var c = cx.Get(cn,Domain.Content)??throw new DBException("42112",cn.ident);
                var ls = new Grouping(cx, BTree<long, object>.Empty + (Grouping.Members,
                    new CTree<long, int>(c.defpos, 0)));
                cx.Add(ls);
                g += (cx,GroupSpecification.Sets,g.sets+ls.defpos);
                simple = true;
                return (GroupSpecification)cx.Add(g);
            }
            simple = false;
            if (Match(Sqlx.LPAREN))
            {
                var lp = LexPos();
                Next();
                if (tok == Sqlx.RPAREN)
                {
                    Next();
                    g += (cx,GroupSpecification.Sets,g.sets+cx.Add(new Grouping(cx)).defpos);
                    return (GroupSpecification)cx.Add(g);
                }
                g +=(cx,GroupSpecification.Sets,g.sets+cx.Add(ParseGroupingSet()).defpos);
                return (GroupSpecification)cx.Add(g);
            }
#if OLAP
            if (Match(Sqlx.GROUPING))
            {
#else
                Mustbe(Sqlx.GROUPING);
#endif
                Next();
                Mustbe(Sqlx.SETS);
                Mustbe(Sqlx.LPAREN);
                g = ParseGroupingElement(g, ref simple);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    g = ParseGroupingElement(g, ref simple);
                }
                Mustbe(Sqlx.RPAREN);
#if OLAP
        }
            var rc = tok;
            Mustbe(Sqlx.ROLLUP, Sqlx.CUBE);
            Mustbe(Sqlx.LPAREN);
            g += (GroupSpecification.Sets,g.sets + ParseGroupingSet(rc));
            while (Match(Sqlx.COMMA))
            {
                Next();
                g +=(GroupSpecification.Sets,g.sets + ParseGroupingSet(rc));
            }
#endif
            return (GroupSpecification)cx.Add(g);
        }
        /// <summary>
        /// a grouping set
        /// </summary>
        /// <returns>the grouping</returns>
        Grouping ParseGroupingSet()
        {
            var cn = ParseIdent();
            var t = new Grouping(cx,BTree<long, object>.Empty
                +(Grouping.Members,new CTree<long,int>(cn.iix.dp,0)));
            var i = 1;
            while (Match(Sqlx.COMMA))
            {
                cn = ParseIdent();
                t+=(Grouping.Members,t.members+(cn.iix.dp,i++));
            }
            Mustbe(Sqlx.RPAREN);
            return (Grouping)cx.Add(t);
        }
        /// <summary>
		/// HavingClause = HAVING BooleanExpr .
        /// </summary>
        /// <returns>The SqlValue (Boolean expression)</returns>
		CTree<long,bool> ParseHavingClause(Domain dm)
        {
            var r = CTree<long,bool>.Empty;
            if (tok != Sqlx.HAVING)
                return r;
            Next();
            var lp = LexPos();
            r = ParseSqlValueDisjunct(Domain.Bool, false, dm);
            if (tok != Sqlx.OR)
                return r;
            var left = Disjoin(r);
            while (tok == Sqlx.OR)
            {
                Next();
                left = (SqlValue)cx.Add(new SqlValueExpr(lp.dp, cx, Sqlx.OR, left, 
                    Disjoin(ParseSqlValueDisjunct(Domain.Bool,false, dm)), Sqlx.NO));
            }
            r +=(left.defpos, true);
      //      lxr.context.cur.Needs(left.alias ?? left.name, RowSet.Need.condition);
            return r;
        }
        /// <summary>
		/// WhereClause = WHERE BooleanExpr .
        /// </summary>
        /// <returns>The SqlValue (Boolean expression)</returns>
		CTree<long,bool>? ParseWhereClause()
		{
            cx.done = ObTree.Empty;
            if (tok != Sqlx.WHERE)
                return null;
			Next();
            var r = ParseSqlValueDisjunct(Domain.Bool, false);
            if (tok != Sqlx.OR)
                return cx.FixTlb(r);
            var left = Disjoin(r);
            while (tok == Sqlx.OR)
            {
                var lp = LexPos();
                Next();
                left = (SqlValue)cx.Add(new SqlValueExpr(lp.dp, cx, Sqlx.OR, left, 
                    Disjoin(ParseSqlValueDisjunct(Domain.Bool,false)), Sqlx.NO));
                left = (SqlValue)cx.Add(left);
            }
            r +=(left.defpos, true);
     //       lxr.context.cur.Needs(left.alias ?? left.name,RowSet.Need.condition);
            return cx.FixTlb(r);
		}
        /// <summary>
		/// WindowClause = WINDOW WindowDef { ',' WindowDef } .
        /// </summary>
        /// <returns>the window set as a tree by window names</returns>
        BTree<string,WindowSpecification> ParseWindowClause()
        {
            if (tok != Sqlx.WINDOW)
                throw new DBException("42000");
            Next();
            var tree = BTree<string,WindowSpecification>.Empty; // of WindowSpecification
            ParseWindowDefinition(ref tree);
            while (tok == Sqlx.COMMA)
            {
                Next();
                ParseWindowDefinition(ref tree);
            }
            return tree;
        }
        /// <summary>
		/// WindowDef = id AS '(' WindowDetails ')' .
        /// </summary>
        /// <param name="tree">ref: the tree of windowdefs</param>
        void ParseWindowDefinition(ref BTree<string,WindowSpecification> tree)
        {
            var id = lxr.val;
            Mustbe(Sqlx.ID);
            Mustbe(Sqlx.AS);
            WindowSpecification r = ParseWindowSpecificationDetails();
            if (r.orderWindow != null)
            {
                if (tree[r.orderWindow] is not WindowSpecification ow)
                    throw new DBException("42135", r.orderWindow).Mix();
                if (ow.order >= 0 && r.order >= 0)
                    throw new DBException("42000", "7.11 SR10d").ISO();
                if (ow.order >= 0)
                    throw new DBException("42000", "7.11 SR10c").ISO();
                if (ow.units != Sqlx.NO || ow.low != null || ow.high != null)
                    throw new DBException("42000", "7.11 SR10e").ISO();
            }
            tree+= (id.ToString(), r);
        }
        /// <summary>
        /// An SQL insert statement
        /// </summary>
        /// <param name="cx">the context</param>
        /// <param name="sql">the sql</param>
        /// <returns>the SqlInsert</returns>
        internal void ParseSqlInsert(string sql)
        {
            lxr = new Lexer(cx,sql);
            tok = lxr.tok;
            if (LexDp() > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
            ParseSqlInsert();
        }
        /// <summary>
		/// Insert = INSERT [WITH string][XMLOption] INTO Table_id [ Cols ]  TypedValue [Classification].
        /// </summary>
        /// <returns>the executable</returns>
        SqlInsert ParseSqlInsert()
        {
            bool with = false;
            var lp = LexPos();
            Next();
            if (tok == Sqlx.WITH)
            {
                Next();
                with = true;
            }
            ParseXmlOption(with);
            Mustbe(Sqlx.INTO);
            Ident ic = new(this);
            cx.IncSD(ic);
            var fm = ParseTableReference(ic.iix.dp);
            cx.Add(fm);
            if (fm is not TableRowSet && !cx.defs.Contains(ic.ident))
                cx.defs += (ic, ic.iix);
            if (cx._Dom(fm) is not Domain dm)
                throw new PEException("PE47193");
            cx.AddDefs(ic, dm);
            Domain? cs = null;
            // Ambiguous syntax here: (Cols) or (Subquery) or other possibilities
            if (tok == Sqlx.LPAREN)
            {
                if (ParseColsList(fm) is Domain cd)
                {
                    dm = new Domain(cx.GetUid(), cx, Sqlx.TABLE, cd.representation, cd.rowType, cd.Length);
                    cs = cd;
                }
                else
                    tok = lxr.PushBack(Sqlx.LPAREN);
            }
            SqlValue sv;
            cs ??= new Domain(cx.GetUid(), cx, Sqlx.ROW, dm.representation, dm.rowType, dm.Length); 
            var vp = cx.GetUid();
            if (tok == Sqlx.DEFAULT)
            {
                Next();
                Mustbe(Sqlx.VALUES);
                sv = SqlNull.Value;
            }
            else
            // care: we might have e.g. a subquery here
                sv = ParseSqlValue(dm);
            if (sv is SqlRow && cx._Dom(sv) is Domain dv) // tolerate a single value without the VALUES keyword
                sv = new SqlRowArray(vp, cx, dv, new BList<long?>(sv.defpos));
            var sce = sv.RowSetFor(vp, cx,dm.rowType,dm.representation) + (RowSet.RSTargets, fm.rsTargets) 
                + (RowSet.Asserts,RowSet.Assertions.AssignTarget);
            cx._Add(sce);
            SqlInsert s = new(lp.dp, fm, sce.defpos, cs); 
            cx.Add(s);
            cx.result = s.value;
            if (Match(Sqlx.SECURITY))
            {
                Next();
                if (cx.db.user?.defpos != cx.db.owner)
                    throw new DBException("42105");
                s += (DBObject.Classification, MustBeLevel());
            }
            cx.DecSD();
            if (cx.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                cx = tr.Execute(s, cx);
            cx.exec = s;
            return (SqlInsert)cx.Add(s);
        }
        /// <summary>
		/// DeleteSearched = DELETE [XMLOption] FROM Table_id [ WhereClause] .
        /// </summary>
        /// <returns>the executable</returns>
		Executable ParseSqlDelete()
        {
            var lp = LexPos();
            Next();
            ParseXmlOption(false);
            Mustbe(Sqlx.FROM);
            Ident ic = new(this);
            cx.IncSD(ic);
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.AS)
                Next();
            if (tok == Sqlx.ID)
            {
                new Ident(this);
                Next();
            }
            var ob = cx.GetObject(ic.ident);
            if (ob == null && cx.defs.Contains(ic.ident))
                ob = cx.obs[cx.defs[(ic.ident,lp.sd)].Item1.dp];
            if (ob == null || cx._Dom(ob)is not Domain dm)
                throw new DBException("42107", ic.ident);
            var f = (RowSet)cx.Add(_From(ic, ob, dm, lp.dp, Grant.Privilege.Delete));
            QuerySearch qs = new(lp.dp, cx, f, ob, Grant.Privilege.Delete);
            cx.defs += (ic, lp);
            cx.GetUid();
            cx.Add(qs);
            var rs = (RowSet?)cx.obs[qs.source]??throw new PEException("PE2006");
            if (ParseWhereClause() is CTree<long, bool> wh)
            {
                rs = (RowSet?)cx.obs[rs.defpos]??throw new PEException("PE2007");
                rs = rs.Apply(RowSet.E + (RowSet._Where, rs.where + wh),cx);
            }
            cx._Add(rs);
            cx.result = rs.defpos;
            if (tok != Sqlx.EOF)
                throw new DBException("42000", tok);
            cx.DecSD();
            if (cx.parse == ExecuteStatus.Obey)
                cx = ((Transaction)cx.db).Execute(qs, cx);
            cx.result = -1L;
            cx.exec = qs;
            return (Executable)cx.Add(qs);
        }
        /// <summary>
        /// the update statement
        /// </summary>
        /// <param name="cx">the context</param>
        /// <param name="sql">the sql</param>
        /// <returns>the updatesearch</returns>
        internal Context ParseSqlUpdate(Context cx, string sql)
        {
            lxr = new Lexer(cx,sql);
            tok = lxr.tok;
            if (LexDp() > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
            return ParseSqlUpdate().Item1;
        }
        /// <summary>
		/// UpdateSearched = UPDATE [XMLOption] Table_id Assignment [WhereClause] .
        /// </summary>
        /// <returns>The UpdateSearch</returns>
		(Context,Executable) ParseSqlUpdate()
        {
            var st = LexPos().dp;
            Next();
            ParseXmlOption(false);
            Ident ic = new(this);
            cx.IncSD(ic);
            Mustbe(Sqlx.ID);
            Mustbe(Sqlx.SET);
            var ob = cx.GetObject(ic.ident);
            if (ob == null && cx.defs.Contains(ic.ident))
                ob = cx.obs[cx.defs[(ic.ident,ic.iix.sd)].Item1.dp];
            if (ob==null || cx._Dom(ob) is not Domain dm)
                throw new DBException("42107", ic.ident);
            var f = (RowSet)cx.Add(_From(ic, ob, dm, st, Grant.Privilege.Update));
            var fd = cx._Dom(f)??throw new PEException("PE48103");
            cx.AddDefs(ic, fd);
            for (var b = fd.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && cx.obs[p] is SqlValue c && c.name!=null)
                {
                    var dp = cx.Ix(p);
                    cx.defs += (new Ident(c.name, dp), dp);
                }
            UpdateSearch us = new(st, cx, f, ob, Grant.Privilege.Update);
            cx.Add(us);
            var ua = ParseAssignments(dm);
            var rs = (RowSet)(cx.obs[us.source]??throw new DBException("PE2009"));
            rs = rs.Apply(new BTree<long,object>(RowSet.Assig, ua),cx);
            if (ParseWhereClause() is CTree<long, bool> wh)
            {
                rs = (RowSet)(cx.obs[rs.defpos]??throw new DBException("PE2010"));
                rs = rs.Apply(new BTree<long, object>(RowSet._Where, wh),cx);
            }
            cx.result = rs.defpos;
            if (cx.parse == ExecuteStatus.Obey)
                cx = ((Transaction)cx.db).Execute(us, cx);
            us = (UpdateSearch)cx.Add(us);
            cx.exec = us;
            cx.DecSD();
            return (cx,us);
        }
        internal CTree<UpdateAssignment,bool> ParseAssignments(string sql,Domain xp)
        {
            lxr = new Lexer(cx,sql);
            tok = lxr.tok;
            return ParseAssignments(xp);
        }
        /// <summary>
        /// Assignment = 	SET Target '='  TypedValue { ',' Target '='  TypedValue }
        /// </summary>
        /// <returns>the list of assignments</returns>
		CTree<UpdateAssignment,bool> ParseAssignments(Domain xp)
		{
            var r = CTree<UpdateAssignment,bool>.Empty + (ParseUpdateAssignment(),true);
            while (tok==Sqlx.COMMA)
			{
				Next();
				r+=(ParseUpdateAssignment(),true);
			}
			return r;
		}
        /// <summary>
        /// Target '='  TypedValue
        /// </summary>
        /// <returns>An updateAssignmentStatement</returns>
		UpdateAssignment ParseUpdateAssignment()
        {
            SqlValue vbl;
            SqlValue val;
            Match(Sqlx.SECURITY);
            if (tok == Sqlx.SECURITY)
            {
                if (cx.db.user?.defpos != cx.db.owner)
                    throw new DBException("42105");
                vbl = (SqlValue)cx.Add(new SqlSecurity(LexPos().dp));
                Next();
            }
            else vbl = ParseVarOrColumn(Domain.Content);
            Mustbe(Sqlx.EQL);
            val = ParseSqlValue(cx._Dom(vbl)??throw new DBException("42000"));
            return new UpdateAssignment(vbl.defpos,val.defpos);
        }
        /// <summary>
        /// Parse an SQL Value
        /// </summary>
        /// <param name="s">The string to parse</param>
        /// <param name="t">the expected obs type if any</param>
        /// <returns>the SqlValue</returns>
        internal SqlValue ParseSqlValue(string s,Domain xp)
        {
            lxr = new Lexer(cx,s);
            tok = lxr.tok;
            return ParseSqlValue(xp);
        }
        internal SqlValue ParseSqlValue(Ident ic, Domain xp)
        {
            lxr = new Lexer(cx,ic.ident,ic.iix.lp);
            tok = lxr.tok;
            return ParseSqlValue(xp);
        }
        /// <summary>
        /// Alas the following informal syntax is not a good guide to the way LL(1) has to go...
		///  Value = 		Literal
        /// |   ID ':'  TypedValue
		/// |	Value BinaryOp TypedValue 
		/// | 	'-'  TypedValue 
		/// |	'('  TypedValue ')'
		/// |	Value Collate 
		/// | 	Value '['  Value ']'
		/// |	ColumnRef  
		/// | 	VariableRef
        /// |   PeriodName
        /// |   PERIOD '('  Value,  Value ')'
		/// |	VALUE 
        /// |   ROW
		/// |	Value '.' Member_id
		/// |	MethodCall
		/// |	NEW MethodCall 
		/// | 	FunctionCall 
		/// |	VALUES  '('  Value { ','  Value } ')' { ',' '('  Value { ','  Value } ')' }
		/// |	Subquery
        /// |   ARRAY Subquery
		/// |	(MULTISET|ARRAY) '['  Value { ','  Value } ']'
        /// |   ROW '(' Value { ',' Value ')'
		/// | 	TABLE '('  Value ')' 
		/// |	TREAT '('  Value AS Sub_Type ')'  .
        /// PeriodName = SYSTEM_TIME | id .
        /// </summary>
        /// <param name="t">a constraint on usage</param>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the SqlValue</returns>
        internal SqlValue ParseSqlValue(Domain xp,bool wfok=false)
        {
            if (tok == Sqlx.PERIOD)
            {
                Next();
                Mustbe(Sqlx.LPAREN);
                var op1 = ParseSqlValue(Domain.UnionDate);
                Mustbe(Sqlx.COMMA);
                var op2 = ParseSqlValue(Domain.UnionDate);
                Mustbe(Sqlx.RPAREN);
                var r = new SqlValueExpr(LexPos().dp, cx, Sqlx.PERIOD, op1, op2, Sqlx.NO);
                return (SqlValue)cx.Add(r);
            }
            SqlValue left;
            if (xp.kind == Sqlx.BOOLEAN || xp.kind == Sqlx.CONTENT)
            {
                left = Disjoin(ParseSqlValueDisjunct(xp, wfok));
                while (cx._Dom(left)?.kind == Sqlx.BOOLEAN && tok == Sqlx.OR)
                {
                    Next();
                    left = new SqlValueExpr(LexPos().dp, cx, Sqlx.OR, left,
                        Disjoin(ParseSqlValueDisjunct(xp, wfok)), Sqlx.NO);
                }
            }
            else if (xp.kind == Sqlx.TABLE || xp.kind == Sqlx.VIEW || xp is NodeType)
            {
                if (Match(Sqlx.TABLE))
                    Next();
                left = ParseSqlTableValue(xp, wfok);
                while (Match(Sqlx.UNION, Sqlx.EXCEPT, Sqlx.INTERSECT))
                {
                    var lp = LexPos();
                    var op = tok;
                    var m = Sqlx.NO;
                    Next();
                    if ((op == Sqlx.UNION || op == Sqlx.EXCEPT)
                        && Match(Sqlx.ALL, Sqlx.DISTINCT))
                    {
                        m = tok;
                        Next();
                    }
                    var right = ParseSqlTableValue(xp, wfok);
                    left = new SqlValueExpr(lp.dp, cx, op, left, right, m);
                }
            }
            else if (xp.kind == Sqlx.TYPE)
            {
                if (Match(Sqlx.LPAREN))
                {
                    Next();
                    if (Match(Sqlx.SELECT))
                    {
                        var cs = ParseCursorSpecification(xp).union;
                        left = new SqlValueSelect(cx.GetUid(), cx,
                            (RowSet)(cx.obs[cs]??throw new DBException("PE2011")),xp);
                    }
                    else
                        left = ParseSqlValue(xp);
                    Mustbe(Sqlx.RPAREN);
                }
                else
                    left = ParseVarOrColumn(xp);
            }
            else
                left = ParseSqlValueExpression(xp, wfok);
            return ((SqlValue)cx.Add(left));
        }
        SqlValue ParseSqlTableValue(Domain xp,bool wfok)
        {
            if (tok == Sqlx.LPAREN)
            {
                Next();
                if (tok == Sqlx.SELECT)
                {
                    var cs = ParseCursorSpecification(xp).union;
                    Mustbe(Sqlx.RPAREN);
                    return (SqlValue)cx.Add(new SqlValueSelect(cx.GetUid(), cx,
                        (RowSet)(cx.obs[cs]??throw new DBException("PE2012")),xp));
                }
            }
            if (Match(Sqlx.SELECT))
                return (SqlValue)cx.Add(new SqlValueSelect(cx.GetUid(),cx,
                    (RowSet)(cx.obs[ParseCursorSpecification(xp).union]??throw new DBException("PE2013")),xp));
            if (Match(Sqlx.VALUES))
            {
                var lp = LexPos();
                Next();
                var v = ParseSqlValueList(xp);
                return (SqlValue)cx.Add(new SqlRowArray(lp.dp, cx, xp, v));
            }
            Mustbe(Sqlx.TABLE);
            return SqlNull.Value; // not reached
        }
        SqlValue Disjoin(CTree<long,bool> s) // s is not empty
        {
            var rb = s.Last();
            var rp = rb?.key() ?? -1L;
            var right = (SqlValue?)cx.obs[rp]??SqlNull.Value;
            for (rb=rb?.Previous();rb!=null;rb=rb.Previous())
                if (cx.obs[rb.key()] is SqlValue lf)
                    right = (SqlValue)cx.Add(new SqlValueExpr(LexPos().dp, cx, Sqlx.AND, 
                        lf, right, Sqlx.NO));
            return (SqlValue)cx.Add(right);
        }
        /// <summary>
        /// Parse a possibly boolean expression
        /// </summary>
        /// <param name="xp"></param>
        /// <param name="wfok"></param>
        /// <param name="dm">A select list to the left of a Having clause, or null</param>
        /// <returns>A disjunction of expressions</returns>
        CTree<long,bool> ParseSqlValueDisjunct(Domain xp,bool wfok, Domain? dm=null)
        {
            var left = ParseSqlValueConjunct(xp, wfok, dm);
            var r = new CTree<long, bool>(left.defpos, true);
            while (cx._Dom(left)?.kind==Sqlx.BOOLEAN && Match(Sqlx.AND))
            {
                Next();
                left = ParseSqlValueConjunct(xp,wfok, dm);
                r += (left.defpos, true);
            }
            return r;
        }
        SqlValue ParseSqlValueConjunct(Domain xp,bool wfok,Domain? dm)
        {
            var left = ParseSqlValueConjunct(xp, wfok);
            return (dm == null) ? left : left.Having(cx,dm);
        }
        SqlValue ParseSqlValueConjunct(Domain xp,bool wfok)
        {
            var left = ParseSqlValueExpression(Domain.Content,wfok);
            if (Match(Sqlx.EQL, Sqlx.NEQ, Sqlx.LSS, Sqlx.GTR, Sqlx.LEQ, Sqlx.GEQ))
            {
                var op = tok;
                var lp = LexPos();
                Next();
                return (SqlValue)cx.Add(new SqlValueExpr(lp.dp, cx,
                    op, left, ParseSqlValueExpression(cx._Dom(left)??Domain.Content,wfok), Sqlx.NO));
            }
            var dl = cx._Dom(left);
            if (xp.kind != Sqlx.CONTENT)
            {
                var nd = dl?.LimitBy(cx, left.defpos, xp);
                if (nd != dl && nd != null)
                    left += (DBObject._Domain, nd.defpos);
            }
            return (SqlValue)cx.Add(left);
        }
        SqlValue ParseSqlValueExpression(Domain xp,bool wfok)
        {
            var left = ParseSqlValueTerm(xp,wfok);
            while ((Domain.UnionDateNumeric.CanTakeValueOf(cx._Dom(left)??Domain.Content)
                ||left.GetType().Name=="SqlValue") 
                && Match(Sqlx.PLUS, Sqlx.MINUS))
            {
                var op = tok;
                var lp = LexPos();
                Next();
                var x = ParseSqlValueTerm(xp, wfok);
                left = (SqlValue)cx.Add(new SqlValueExpr(lp.dp, cx, op, left, x, Sqlx.NO));
            }
            return (SqlValue)cx.Add(left);
        }
        /// <summary>
        /// |   NOT TypedValue
        /// |	Value BinaryOp TypedValue 
        /// |   PeriodPredicate
		/// BinaryOp =	'+' | '-' | '*' | '/' | '||' | MultisetOp | AND | OR | LT | GT | LEQ | GEQ | EQL | NEQ. 
		/// MultisetOp = MULTISET ( UNION | INTERSECT | EXCEPT ) ( ALL | DISTINCT ) .
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the sqlValue</returns>
        SqlValue ParseSqlValueTerm(Domain xp,bool wfok)
        {
            bool sign = false, not = false;
            var lp = LexPos();
            if (tok == Sqlx.PLUS)
                Next();
            else if (tok == Sqlx.MINUS)
            {
                Next();
                sign = true;
            }
            else if (tok == Sqlx.NOT)
            {
                Next();
                not = true;
            }	
            var left = ParseSqlValueFactor(xp,wfok);
            if (sign)
                left = new SqlValueExpr(lp.dp, cx, Sqlx.MINUS, null, left, Sqlx.NO)
                    .Constrain(cx,Domain.UnionNumeric);
            else if (not)
                left = left.Invert(cx);
            var imm = Sqlx.NO;
            if (Match(Sqlx.IMMEDIATELY))
            {
                Next();
                imm = Sqlx.IMMEDIATELY;
            }
            if (Match(Sqlx.CONTAINS, Sqlx.OVERLAPS, Sqlx.EQUALS, Sqlx.PRECEDES, Sqlx.SUCCEEDS))
            {
                var op = tok;
                lp = LexPos();
                Next();
                return (SqlValue)cx.Add(new SqlValueExpr(lp.dp, cx,
                    op, left, ParseSqlValueFactor(cx._Dom(left)??Domain.Content,wfok), imm));
            }
            while (Match(Sqlx.TIMES, Sqlx.DIVIDE,Sqlx.MULTISET))
            {
                Sqlx op = tok;
                lp = LexPos();
                switch (op)
                {
                    case Sqlx.TIMES:
                        break;
                    case Sqlx.DIVIDE: goto case Sqlx.TIMES;
                    case Sqlx.MULTISET:
                        {
                            Next();
                            if (Match(Sqlx.INTERSECT))
                                op = tok;
                            else
                            {
                                tok = lxr.PushBack(Sqlx.MULTISET);
                                return (SqlValue)cx.Add(left);
                            }
                        }
                        break;
                }
                Sqlx m = Sqlx.NO;
                if (Match(Sqlx.ALL, Sqlx.DISTINCT))
                {
                    m = tok;
                    Next();
                }
                Next();
                var ld = cx._Dom(left)??Domain.Content;
                if (ld.kind == Sqlx.TABLE)
                    ld = Domain.Content; // must be scalar
                left = (SqlValue)cx.Add(new SqlValueExpr(lp.dp, cx, op, left, 
                    ParseSqlValueFactor(ld,wfok), m));
            }
            return (SqlValue)cx.Add(left);
        }
        /// <summary>
        /// |	Value '||'  TypedValue 
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the SqlValue</returns>
		SqlValue ParseSqlValueFactor(Domain xp,bool wfok)
		{
			var left = ParseSqlValueEntry(xp,wfok);
			while (Match(Sqlx.CONCATENATE))
			{
				Sqlx op = tok;
				Next();
				var right = ParseSqlValueEntry(cx._Dom(left)??Domain.Content,wfok);
				left = new SqlValueExpr(LexPos().dp, cx, op,left,right,Sqlx.NO);
                cx.Add(left);
			}
			return left;
		}
        /// <summary>
        /// | 	Value '['  TypedValue ']'
        /// |	ColumnRef  
        /// | 	VariableRef 
        /// |   TypedValue
        /// |   PeriodRef | SYSTEM_TIME
        /// |   id ':'  Value
        /// |	Value '.' Member_id
        /// |   Value IN '(' RowSet | (Value {',' Value })')'
        /// |   Value IS [NOT] NULL
        /// |   Value IS [NOT] MEMBER OF Value
        /// |   Value IS [NOT] BETWEEN Value AND Value
        /// |   Value IS [NOT] OF '(' [ONLY] id {',' [ONLY] id } ')'
        /// |   Value IS [NOT] LIKE Value [ESCAPE TypedValue ]
        /// |   Value COLLATE id
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the sqlValue</returns>
		SqlValue ParseSqlValueEntry(Domain xp,bool wfok)
        {
            var left = ParseSqlValueItem(xp,wfok);
            bool invert = false;
            var lp = LexPos();
            if (tok == Sqlx.COLON)
            {
                var fl = left;
                if (fl == null)
                    throw new DBException("42000", left.ToString()).ISO();
                Next();
                left = ParseSqlValueItem(xp,wfok);
                // ??
            }
            while (tok==Sqlx.DOT || tok==Sqlx.LBRACK)
                if (tok==Sqlx.DOT)
                {
                    // could be table alias, block id, instance id etc
                    Next();
                    if (tok == Sqlx.TIMES)
                    {
                        lp = LexPos();
                        Next();
                        return new SqlStar(lp.dp, left.defpos);
                    }
                    var n = new Ident(this);
                    Mustbe(Sqlx.ID);
                    if (tok == Sqlx.LPAREN)
                    {
                        var ps = BList<long?>.Empty;
                        Next();
                        if (tok != Sqlx.RPAREN)
                            ps = ParseSqlValueList(xp);
                        cx.Add(left);
                        var ut = left.domain;
                        if (cx._Ob(ut) is not DBObject uo || uo.infos[cx.role.defpos] is not ObInfo oi)
                            throw new DBException("42105");
                        var ar = cx.Signature(ps);
                        var pr = cx.db.objects[oi.methodInfos[n.ident]?[ar] ?? -1L] as Method
                            ?? throw new DBException("42173", n);
                        var cs = new CallStatement(lp.dp, cx, pr, n.ident, ps, left);
                        cx.Add(cs);
                        Mustbe(Sqlx.RPAREN);
                        left = new SqlMethodCall(n.iix.dp, cs);
                        left = (SqlValue)cx.Add(left);
                    }
                    else
                    {
                        var dm = cx._Dom(left);
                        var oi = dm?.infos[cx.role.defpos];
                        var cp = -1L;
                        for (var b = dm?.rowType.First(); cp < 0 && b != null; b = b.Next())
                            if (b.value() is long p && cx._Ob(p) is DBObject ob && ob.infos[cx.role.defpos] is ObInfo ci &&
                                    ci.name == n.ident)
                                cp = p;
                        var el = (SqlValue)cx.Add(new SqlLiteral(n.iix.dp, new TInt(cp)));
                        left = new SqlValueExpr(lp.dp, cx, Sqlx.DOT, left, el,Sqlx.NO);
                    }
                } else // tok==Sqlx.LBRACK
                {
                    Next();
                    left = new SqlValueExpr(lp.dp, cx, Sqlx.LBRACK, left,
                        ParseSqlValue(Domain.Int), Sqlx.NO);
                    Mustbe(Sqlx.RBRACK);
                }

            if (tok == Sqlx.IS)
            {
                if (!xp.CanTakeValueOf(Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                bool b = true;
                if (tok == Sqlx.NOT)
                {
                    Next();
                    b = false;
                }
                if (tok == Sqlx.OF)
                {
                    Next();
                    Mustbe(Sqlx.LPAREN);
                    var r = BList<Domain>.Empty;
                    var t1 = ParseSqlDataType();
                    lp = LexPos();
                    r+=t1;
                    while (tok == Sqlx.COMMA)
                    {
                        Next();
                        t1 = ParseSqlDataType();
                        lp = LexPos();
                        r+=t1;
                    }
                    Mustbe(Sqlx.RPAREN);
                    return (SqlValue)cx.Add(new TypePredicate(lp.dp,left, b, r));
                }
                Mustbe(Sqlx.NULL);
                return (SqlValue)cx.Add(new NullPredicate(lp.dp,left, b));
            }
            var savestart = lxr.start;
            if (tok == Sqlx.NOT)
            {
                if (!xp.CanTakeValueOf(Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                invert = true;
            }
            if (tok == Sqlx.BETWEEN)
            {
                if (!xp.CanTakeValueOf(Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                var od = cx._Dom(left)??Domain.Content;
                var lw = ParseSqlValueTerm(od, false);
                Mustbe(Sqlx.AND);
                var hi = ParseSqlValueTerm(od, false);
                return (SqlValue)cx.Add(new BetweenPredicate(lp.dp, cx, left, !invert, lw, hi));
            }
            if (tok == Sqlx.LIKE)
            {
                if (!(xp.CanTakeValueOf(Domain.Bool) && 
                    Domain.Char.CanTakeValueOf(cx._Dom(left)??Domain.Content)))
                    throw new DBException("42000", lxr.pos);
                Next();
                LikePredicate k = new (lp.dp,cx, left, !invert,ParseSqlValue(Domain.Char), null);
                if (tok == Sqlx.ESCAPE)
                {
                    Next();
                    k+=(LikePredicate.Escape,ParseSqlValueItem(Domain.Char, false)?.defpos??-1L);
                }
                return (SqlValue)cx.Add(k);
            }
#if SIMILAR
            if (tok == Sqlx.LIKE_REGEX)
            {
                            if (!cx._Domain(tp).CanTakeValueOf(Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                SimilarPredicate k = new SimilarPredicate(left, !invert, ParseSqlValue(), null, null);
                if (Match(Sqlx.FLAG))
                {
                    Next();
                    k.flag = ParseSqlValue(-1);
                }
                return (SqlValue)cx.Add(k);
            }
            if (tok == Sqlx.SIMILAR)
            {
                            if (!cx._Domain(tp).CanTakeValueOf(Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                Mustbe(Sqlx.TO);
                SimilarPredicate k = new SimilarPredicate(left, !invert, ParseSqlValue(), null, null);
                if (Match(Sqlx.ESCAPE))
                {
                    Next();
                    k.escape = ParseSqlValue(Domain.Char.defpos);
                }
                return (SqlValue)cx.Add(k);
            }
#endif
            if (tok == Sqlx.IN)
            {
                if (!xp.CanTakeValueOf(Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                InPredicate n = new InPredicate(lp.dp, cx, left)+
                    (QuantifiedPredicate.Found, !invert);
                Mustbe(Sqlx.LPAREN);
                if (Match(Sqlx.SELECT, Sqlx.TABLE, Sqlx.VALUES))
                {
                    RowSet rs = ParseQuerySpecification(Domain.TableType);
                    cx.Add(rs);
                    n += (QuantifiedPredicate._Select,rs.defpos);
                } 
                else
                    n += (QuantifiedPredicate.Vals, ParseSqlValueList(cx._Dom(left)??Domain.Content));
                Mustbe(Sqlx.RPAREN);
                return (SqlValue)cx.Add(n);
            }
            if (tok == Sqlx.MEMBER)
            {
                if (!xp.CanTakeValueOf(Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                Mustbe(Sqlx.OF);
                var dm = (Domain)cx.Add(new Domain(cx.GetUid(),Sqlx.MULTISET, xp));
                return (SqlValue)cx.Add(new MemberPredicate(LexPos().dp, cx,left,
                    !invert, ParseSqlValue(dm)));
            }
            if (invert)
            {
                tok = lxr.PushBack(Sqlx.NOT);
                lxr.pos = lxr.start-1;
                lxr.start = savestart;
            }
            else
            if (tok == Sqlx.COLLATE)
                left = ParseCollateExpr(left);
            return (SqlValue)cx.Add(left);
		}
        /// <summary>
        /// |	Value Collate 
        /// </summary>
        /// <param name="e">The SqlValue</param>
        /// <returns>The collated SqlValue</returns>
        SqlValue ParseCollateExpr(SqlValue e)
        {
            Next();
            var o = lxr.val;
            Mustbe(Sqlx.ID);
            var ci = new CultureInfo(o.ToString());
            var dm = cx._Dom(e) ?? Domain.Null;
            dm = (Domain)dm.New(cx,dm.mem+(Domain.Culture,ci));
            return (SqlValue)cx.Add(e + (DBObject._Domain, dm.defpos));
        }
        /// <summary>
        ///  Value= [NEW} MethodCall
        /// | 	FunctionCall 
        /// |	VALUES  '('  Value { ','  Value } ')' { ',' '('  Value { ','  Value } ')' }
        /// |	Subquery
        /// |   TypedValue
        /// |   ARRAY Subquery
        /// |	( MULTISET | ARRAY ) '['  Value { ','  Value } ']'
        /// |   ROW '(' Value { ',' Value } ')'
        /// | 	TABLE '('  Value ')' 
        /// |	TREAT '('  Value AS Sub_Type ')'  
        /// |   '[' DocArray ']'
        /// |   '{' Document '}'
        /// |   '$lt;' Xml '$gt;' 
        /// Predicate = Any | At | Between | Comparison | QuantifiedComparison | Current | Every | Exists | In | Like | Member | Null | Of 
        /// | Some | Unique .
        /// Any = ANY '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
        /// At = ColumnRef AT TypedValue .
        /// Between =  TypedValue [NOT] BETWEEN [SYMMETRIC|ASYMMETRIC]  TypedValue AND TypedValue .
        /// Comparison =  TypedValue CompOp TypedValue .
        /// CompOp = '=' | '<>' | '<' | '>' | '<=' | '>=' .
        /// QuantifiedComparison =  TypedValue CompOp (ALL|SOME|ANY) Subquery .
        /// Current = CURRENT '(' ColumnRef ')'.
        /// Current and At can be used on default temporal TableColumns of temporal tables. See section 7.12.
        /// Every = EVERY '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
        /// Exists = EXISTS QueryExpression .
        /// FuncOpt = [FILTER '(' WHERE SearchCondition ')'] [OVER WindowSpec] .
        /// The presence of the OVER keyword makes a window function. In accordance with SQL2003-02 section 4.15.3, window functions can only be used in the select list of a RowSetSpec or SelectSingle or the order by clause of a �simple table query� as defined in section 7.5 above. Thus window functions cannot be used within expressions or as function arguments.
        /// In =  TypedValue [NOT] IN '(' QueryExpression | (  TypedValue { ','  TypedValue } ) ')' .
        /// Like =  TypedValue [NOT] LIKE string .
        /// Member =  TypedValue [ NOT ] MEMBER OF TypedValue .
        /// Null =  TypedValue IS [NOT] NULL .
        /// Of =  TypedValue IS [NOT] OF '(' [ONLY] Type {','[ONLY] Type } ')' .
        /// Some = SOME '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
        /// Unique = UNIQUE QueryExpression .
        /// VariableRef =	{ Scope_id '.' } Variable_id .
        /// ColumnRef =	[ TableOrAlias_id '.' ]  Column_id 
        /// 	| TableOrAlias_id '.' (ROW | PARTITION | VERSIONING | CHECK) .
		/// FunctionCall = NumericValueFunction | StringValueFunction | DateTimeFunction | SetFunctions | XMLFunction | UserFunctionCall | MethodCall .
		/// NumericValueFunction = AbsolutValue | Avg | Cast | Ceiling | Coalesce | Correlation | Count | Covariance | Exponential | Extract | Floor | Grouping | Last | LengthExpression | Maximum | Minimum | Modulus 
        ///     | NaturalLogarithm | Next | Nullif | Percentile | Position | PowerFunction | Rank | Regression | RowNumber | SquareRoot | StandardDeviation | Sum | Variance | HttpGet .
		/// AbsolutValue = ABS '('  TypedValue ')' .
		/// Avg = AVG '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
		/// Cast = CAST '('  TypedValue AS Type ')' .
		/// Ceiling = (CEIL|CEILING) '('  TypedValue ')' .
		/// Coalesce = COALESCE '('  TypedValue {','  TypedValue } ')'
		/// Corelation = CORR '('  TypedValue ','  TypedValue ')' FuncOpt .
		/// Count = COUNT '(' '*' ')'
		/// | COUNT '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
		/// Covariance = (COVAR_POP|COVAR_SAMP) '('  TypedValue ','  TypedValue ')' FuncOpt .
        /// Schema = SCHEMA '(' ObjectName ')' . 
		/// WindowSpec = Window_id | '(' WindowDetails ')' .
		/// Exponential = EXP '('  TypedValue ')' .
		/// Extract = EXTRACT '(' ExtractField FROM TypedValue ')' .
		/// ExtractField =  YEAR | MONTH | DAY | HOUR | MINUTE | SECOND.
		/// Floor = FLOOR '('  TypedValue ')' .
		/// Grouping = GROUPING '(' ColumnRef { ',' ColumnRef } ')' .
		/// Last = LAST ['(' ColumnRef ')' OVER WindowSpec ] .
		/// LengthExpression = (CHAR_LENGTH|CHARACTER_LENGTH|OCTET_LENGTH) '('  TypedValue ')' .
		/// Maximum = MAX '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
		/// Minimum = MIN '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
		/// Modulus = MOD '('  TypedValue ','  TypedValue ')' .
		/// NaturalLogarithm = LN '('  TypedValue ')' .
		/// Next = NEXT ['(' ColumnRef ')' OVER WindowSpec ] .
		/// Nullif = NULLIF '('  TypedValue ','  TypedValue ')' .
		/// Percentile = (PERCENTILE_CONT|PERCENTILE_DISC) '('  TypedValue ')' WithinGroup .
		/// Position = POSITION ['('Value IN TypedValue ')'] .
		/// PowerFunction = POWER '('  TypedValue ','  TypedValue ')' .
		/// Rank = (CUME_DIST|DENSE_RANK|PERCENT_RANK|RANK) '('')' OVER WindowSpec 
		///   | (DENSE_RANK|PERCENT_RANK|RANK|CUME_DIST) '('  TypedValue {','  TypedValue } ')' WithinGroup .
		/// Regression = (REGR_SLOPE|REGR_INTERCEPT|REGR_COUNT|REGR_R2|REGR_AVVGX| REGR_AVGY|REGR_SXX|REGR_SXY|REGR_SYY) '('  TypedValue ','  TypedValue ')' FuncOpt .
		/// RowNumber = ROW_NUMBER '('')' OVER WindowSpec .
		/// SquareRoot = SQRT '('  TypedValue ')' .
		/// StandardDeviation = (STDDEV_POP|STDDEV_SAMP) '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
		/// Sum = SUM '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
		/// DateTimeFunction = CURRENT_DATE | CURRENT_TIME | LOCALTIME | CURRENT_TIMESTAMP | LOCALTIMESTAMP .
		/// StringValueFunction = Substring | Fold | Transcode | Transliterate | Trim | Overlay | Normalise | TypeUri | XmlAgg .
		/// Substring = SUBSTRING '('  TypedValue FROM TypedValue [ FOR TypedValue ] ')' .
		/// Fold = (UPPER|LOWER) '('  TypedValue ')' .
		/// Trim = TRIM '(' [[LEADING|TRAILING|BOTH] [character] FROM]  TypedValue ')' .
		/// Variance = (VAR_POP|VAR_SAMP) '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
        /// TypeUri = TYPE_URI '('  TypedValue ')' .
		/// XmlAgg = XMLAGG '('  TypedValue ')' .
		/// SetFunction = Cardinality | Collect | Element | Fusion | Intersect | Set .
		/// Collect = CARDINALITY '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt  .
		/// Fusion = FUSION '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt  .
		/// Intersect = INTERSECT '(' [DISTINCT|ALL]  TypedValue) ')' FuncOpt .
		/// Cardinality = CARDINALITY '('  TypedValue ')' .
		/// Element = ELEMENT '('  TypedValue ')' .
		/// Set = SET '('  TypedValue ')' .
		/// XMLFunction = 	XMLComment | XMLConcat | XMLElement | XMLForest | XMLParse | XMLProc | XMLRoot | XMLAgg | XPath .
		/// XPath is not in the SQL2003 standard but has become popular. See section 5.9.
		/// XMLComment = XMLCOMMENT '('  TypedValue ')' .
		/// XMLConcat = XMLCONCAT '('  TypedValue {','  TypedValue } ')' .
		/// XMLElement = XMLELEMENT '(' NAME id [ ',' Namespace ] [',' AttributeSpec ]{ ','  TypedValue } ')' .
		/// Namespace = XMLNAMESPACES '(' NamespaceDefault |( string AS id {',' string AS id }) ')' .
		/// NamespaceDefault = (DEFAULT string) | (NO DEFAULT) .
		/// AttributeSpec = XMLATTRIBUTES '(' NamedValue {',' NamedValue }')' .
		/// NamedValue =  TypedValue [ AS id ] .
		/// XMLForest = XMLFOREST '(' [ Namespace ','] NamedValue { ',' NamedValue } ')' .
		/// XMLParse = XMLPARSE '(' CONTENT TypedValue ')' .
		/// XMLProc = XMLPI '(' NAME id [','  TypedValue ] ')' .
		/// XMLRoot = XMLROOT '('  TypedValue ',' VERSION (TypedValue | NO VALUE) [','STANDALONE (YES|NO|NO VALUE)] ')' .
		/// NO VALUE is the default for the standalone property.
		/// XPath = XMLQUERY '('  TypedValue ',' xml ')' .
        /// HttpGet = HTTP GET url_Value [AS mime_string] .
        /// Level = LEVEL id ['-' id] GROUPS { id } REFERENCES { id }.
		/// MethodCall = 	Value '.' Method_id  [ '(' [  TypedValue { ','  TypedValue } ] ')']
		/// 	|	'('  TypedValue AS Type ')' '.' Method_id  [ '(' [  TypedValue { ','  TypedValue } ] ')']
		///     |	Type'::' Method_id [ '(' [  TypedValue { ','  TypedValue } ] ')' ] .
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <param name="wfok">whether a window function is allowed</param>
        /// <returns>the sql value</returns>
        internal SqlValue ParseSqlValueItem(Domain xp,bool wfok)
        {
            SqlValue r;
            var lp = LexPos();
            if (tok == Sqlx.QMARK && cx.parse == ExecuteStatus.Prepare)
            {
                Next();
                var qm = new SqlLiteral(lp.dp, new TQParam(Domain.Content,lp));
                cx.qParams += qm.defpos;
                return qm;
            }
            if (Match(Sqlx.LEVEL))
            {
                return (SqlValue)cx.Add(new SqlLiteral(LexPos().dp, TLevel.New(MustBeLevel())));
            }
            Match(Sqlx.SCHEMA); // for Pyrrho 5.1 most recent schema change
            if (Match(Sqlx.ID,Sqlx.FIRST,Sqlx.NEXT,Sqlx.LAST,Sqlx.CHECK,
                Sqlx.PROVENANCE,Sqlx.TYPE_URI)) // ID or pseudo ident
            {
                SqlValue vr = ParseVarOrColumn(xp);
                if (tok == Sqlx.DOUBLECOLON)
                {
                    Next();
                    if (vr.name==null || cx.db.objects[cx.role.dbobjects[vr.name]??-1L] is not Domain ut
                        || ut.infos[cx.role.defpos] is not ObInfo oi)
                        throw new DBException("42139",vr.name??"??").Mix();
                    var name = new Ident(this);
                    Mustbe(Sqlx.ID);
                    lp = LexPos();
                    Mustbe(Sqlx.LPAREN);
                    var ps = ParseSqlValueList(xp);
                    Mustbe(Sqlx.RPAREN);
                    var n = cx.Signature(ps);
                    var m = cx.db.objects[oi.methodInfos[name.ident]?[n]??-1L] as Method
                        ?? throw new DBException("42132",name.ident,ut.name).Mix();
                    if (m.methodType != PMethod.MethodType.Static)
                        throw new DBException("42140").Mix();
                    var fc = new CallStatement(lp.dp, cx, m, name.ident, ps, vr);
                    return (SqlValue)cx.Add(new SqlProcedureCall(name.iix.dp, fc));
                }
                return (SqlValue)cx.Add(vr);
            }
            if (Match(Sqlx.EXISTS,Sqlx.UNIQUE))
            {
                Sqlx op = tok;
                Next();
                Mustbe(Sqlx.LPAREN);
                RowSet g = ParseQueryExpression(Domain.Null);
                Mustbe(Sqlx.RPAREN);
                if (op == Sqlx.EXISTS)
                    return (SqlValue)cx.Add(new ExistsPredicate(LexPos().dp, cx, g));
                else
                    return (SqlValue)cx.Add(new UniquePredicate(LexPos().dp, cx, g));
            }
            if (Match(Sqlx.RDFLITERAL, Sqlx.DOCUMENTLITERAL, Sqlx.CHARLITERAL, 
                Sqlx.INTEGERLITERAL, Sqlx.NUMERICLITERAL, Sqlx.NULL,
            Sqlx.REALLITERAL, Sqlx.BLOBLITERAL, Sqlx.BOOLEANLITERAL))
            {
                r = new SqlLiteral(LexDp(), lxr.val);
                Next();
                return (SqlValue)cx.Add(r);
            }
            // pseudo functions
            switch (tok)
            {
                case Sqlx.ARRAY:
                    {
                        Next();
                        if (Match(Sqlx.LPAREN))
                        {
                            lp = LexPos();
                            Next();
                            if (tok == Sqlx.SELECT)
                            {
                                var st = lxr.start;
                                var cs = ParseCursorSpecification(Domain.Null).union;
                                Mustbe(Sqlx.RPAREN);
                                return (SqlValue)cx.Add(new SqlValueSelect(lp.dp, cx, 
                                    (RowSet)(cx.obs[cs]??throw new DBException("42000")),xp)); 
                            }
                            throw new DBException("22204");
                        }
                        Mustbe(Sqlx.LBRACK);
                        var et = (xp.kind == Sqlx.CONTENT) ? xp :
                            cx._Dom(xp.elType)??
                            throw new DBException("42000", lxr.pos);
                        var v = ParseSqlValueList(et);
                        if (v.Length == 0)
                            throw new DBException("22103").ISO();
                        Mustbe(Sqlx.RBRACK);
                        return (SqlValue)cx.Add(new SqlValueArray(lp.dp, xp, v));
                    }
                 case Sqlx.SCHEMA:
                    {
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        var (ob,n) = ParseObjectName();
                        if (Match(Sqlx.COLUMN))
                        {
                            Next();
                            var cn = lxr.val;
                            Mustbe(Sqlx.ID);
                            if (ob is not Table tb)
                                throw new DBException("42107", n).Mix();
                            if (cx._Dom(tb.defpos) is not Domain ft ||
                                cx.db.objects[ft.ColFor(cx, cn.ToString())] is not DBObject oc)
                                    throw new DBException("42112", cn.ToString());
                            ob = oc;
                        }
                        r = new SqlLiteral(lp.dp, new TInt(ob.lastChange));
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(r);
                    } 
                case Sqlx.CURRENT_DATE: 
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp.dp, cx,Sqlx.CURRENT_DATE, 
                            null, null,null,Sqlx.NO));
                    }
                case Sqlx.CURRENT_ROLE:
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp.dp, cx,Sqlx.CURRENT_ROLE, 
                            null, null, null, Sqlx.NO));
                    }
                case Sqlx.CURRENT_TIME:
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp.dp, cx,Sqlx.CURRENT_TIME, 
                            null, null, null, Sqlx.NO));
                    }
                case Sqlx.CURRENT_TIMESTAMP: 
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp.dp, cx,Sqlx.CURRENT_TIMESTAMP, 
                            null, null, null, Sqlx.NO));
                    }
                case Sqlx.USER:
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp.dp, cx,Sqlx.USER, 
                            null, null, null, Sqlx.NO));
                    }
                case Sqlx.DATE: // also TIME, TIMESTAMP, INTERVAL
                    {
                        Sqlx tk = tok;
                        Next();
                        var o = lxr.val;
                        lp = LexPos();
                        if (tok == Sqlx.CHARLITERAL)
                        {
                            Next();
                            return new SqlDateTimeLiteral(lp.dp,cx,Domain.For(tk), o.ToString());
                        }
                        else
                            return (SqlValue)cx.Add(new SqlLiteral(lp.dp,o));
                    }
                case Sqlx.INTERVAL:
                    {
                        Next();
                        var o = lxr.val;
                        Mustbe(Sqlx.CHARLITERAL);
                        Domain di = ParseIntervalType();
                        return (SqlValue)cx.Add(new SqlDateTimeLiteral(lp.dp, cx,di, o.ToString()));
                    }
                case Sqlx.LPAREN:// subquery
                    {
                        Next();
                        if (tok == Sqlx.SELECT)
                        {
                            var st = lxr.start;
                            var cs = ParseCursorSpecification(xp).union;
                            Mustbe(Sqlx.RPAREN);
                            return (SqlValue)cx.Add(new SqlValueSelect(cx.GetUid(), 
                                cx,(RowSet)(cx.obs[cs]??throw new PEException("PE2010")),xp));
                        }
                        Domain et = Domain.Null;
                        switch(xp.kind)
                        {
                            case Sqlx.ARRAY:
                            case Sqlx.MULTISET:
                                et = xp.elType??Domain.Null;
                                break;
                            case Sqlx.CONTENT:
                                et = Domain.Content;
                                break;
                            case Sqlx.ROW:
                                break;
                            default:
                                var v = ParseSqlValue(xp);
                                if (v is SqlLiteral sl)
                                    v = (SqlValue)cx.Add(new SqlLiteral(lp.dp, xp.Coerce(cx, sl.val)));
                                Mustbe(Sqlx.RPAREN);
                                return v;
                        }
                        var fs = BList<DBObject>.Empty;
                        for (var i = 0; ; i++)
                        {
                            var it = ParseSqlValue(et??
                                cx._Dom(xp.representation[xp[i]??-1L])??Domain.Content);
                            if (tok == Sqlx.AS)
                            {
                                lp = LexPos();
                                Next();
                                var ic = new Ident(this);
                                Mustbe(Sqlx.ID);
                                it += (DBObject._Alias, ic.ToString());
                                cx.Add(it);
                            }
                            fs += it;
                            if (tok != Sqlx.COMMA)
                                break;
                            Next();
                        }
                        Mustbe(Sqlx.RPAREN);
                        if (fs.Length==1 && fs[0] is SqlValue w)
                            return (SqlValue)cx.Add(w);
                        return (SqlValue)cx.Add(new SqlRow(lp.dp,cx,fs)); 
                    }
                case Sqlx.MULTISET:
                    {
                        Next();
                        if (Match(Sqlx.LPAREN))
                            return ParseSqlValue(xp);
                        Mustbe(Sqlx.LBRACK);
                        var v = ParseSqlValueList(xp);
                        if (v.Length == 0)
                            throw new DBException("22103").ISO();
                        Mustbe(Sqlx.RBRACK);
                        return (SqlValue)cx.Add(new SqlValueArray(lp.dp, xp, v));
                    }
                case Sqlx.NEW:
                    {
                        Next();
                        var o = new Ident(this);
                        Mustbe(Sqlx.ID);
                        lp = LexPos();
                        if (cx.db.objects[cx.role.dbobjects[o.ident]??-1L] is not Domain ut
                            || ut.infos[cx.role.defpos] is not ObInfo oi)
                            throw new DBException("42142").Mix();
                        Mustbe(Sqlx.LPAREN);
                        var ps = ParseSqlValueList(ut);
                        var n = cx.Signature(ps);
                        Mustbe(Sqlx.RPAREN);
                        if (cx.db.objects[oi.methodInfos[o.ident]?[n] ?? -1L] is not Method m)
                        {
                            if (ut.Length != 0 && ut.Length != (int)n.Count)
                                throw new DBException("42142").Mix();
                            return (SqlValue)cx.Add(new SqlDefaultConstructor(o.iix.dp, cx, ut, ps));
                        }
                        if (m.methodType != PMethod.MethodType.Constructor)
                            throw new DBException("42142").Mix();
                        var fc = new CallStatement(lp.dp, cx, m, o.ident, ps);
                        return (SqlValue)cx.Add(new SqlProcedureCall(o.iix.dp, fc));
                    }
                case Sqlx.ROW:
                    {
                        Next();
                        if (Match(Sqlx.LPAREN))
                        {
                            lp = LexPos();
                            Next();
                            var v = ParseSqlValueList(xp);
                            Mustbe(Sqlx.RPAREN);
                            return (SqlValue)cx.Add(new SqlRow(lp.dp,cx,xp,v));
                        }
                        throw new DBException("42135", "ROW").Mix();
                    }
                /*       case Sqlx.SELECT:
                           {
                               var sc = new SaveContext(trans, ExecuteStatus.Parse);
                               RowSet cs = ParseCursorSpecification(t).stmt as RowSet;
                               sc.Restore(tr);
                               return (SqlValue)cx.Add(new SqlValueSelect(cs, t));
                           } */
                case Sqlx.TABLE: // allowed by 6.39
                    {
                        Next();
                        var lf = ParseSqlValue(Domain.TableType);
                        return (SqlValue)cx.Add(lf);
                    }

                case Sqlx.TIME: goto case Sqlx.DATE;
                case Sqlx.TIMESTAMP: goto case Sqlx.DATE;
                case Sqlx.TREAT:
                    {
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        var v = ParseSqlValue(Domain.Content);
                        Mustbe(Sqlx.RPAREN);
                        Mustbe(Sqlx.AS);
                        var dt = ParseSqlDataType();
                        return (SqlValue)cx.Add(new SqlTreatExpr(lp.dp, v, dt));//.Needs(v);
                    }
                case Sqlx.CASE:
                    {
                        Next();
                        SqlValue? v = null;
                        Domain cp = Domain.Bool;
                        Domain rd = Domain.Content;
                        if (tok != Sqlx.WHEN)
                        {
                            v = ParseSqlValue(xp);
                            cx.Add(v);
                            cp = cx._Dom(v)??cp;
                        }
                        var cs = BList<(long, long)>.Empty;
                        var wh = BList<long?>.Empty;
                        while (Mustbe(Sqlx.WHEN, Sqlx.ELSE) == Sqlx.WHEN)
                        {
                            var w = ParseSqlValue(cp);
                            cx.Add(w);
                            wh += w.defpos;
                            while (v != null && tok == Sqlx.COMMA)
                            {
                                Next();
                                w = ParseSqlValue(cp);
                                cx.Add(w);
                                wh += w.defpos;
                            }
                            Mustbe(Sqlx.THEN);
                            var x = ParseSqlValue(xp);
                            cx.Add(x);
                            rd = rd.Constrain(cx, lp.dp, cx._Dom(x) ?? Domain.Content);
                            for (var b = wh.First(); b != null; b = b.Next())
                                if (b.value() is long p)
                                    cs += (p, x.defpos);
                        }
                        var el = ParseSqlValue(xp);
                        cx.Add(el);
                        Mustbe(Sqlx.END);
                        return (SqlValue)cx.Add((v == null) ? (SqlValue)new SqlCaseSearch(lp.dp, rd, cs, el.defpos)
                            : new SqlCaseSimple(lp.dp, rd, v, cs, el.defpos));
                    }
                case Sqlx.VALUE:
                    {
                        Next();
                        SqlValue vbl = new(new Ident("VALUE",lp),xp);
                        return (SqlValue)cx.Add(vbl);
                    }
                case Sqlx.VALUES:
                    {
                        var v = ParseSqlValueList(xp);
                        return (SqlValue)cx.Add(new SqlRowArray(lp.dp, cx, xp, v));
                    }
                case Sqlx.LBRACE:
                    {
                        var v = BList<DBObject>.Empty;
                        Next();
                        if (tok != Sqlx.RBRACE)
                        {
                            var (n,sv) = GetDocItem(cx);
                            v += cx.Add(sv+(ObInfo.Name,n));
                        }
                        while (tok==Sqlx.COMMA)
                        {
                            Next();
                            var (n,sv) = GetDocItem(cx);
                            v += cx.Add(sv + (ObInfo.Name, n));
                        }
                        Mustbe(Sqlx.RBRACE);
                        return (SqlValue)cx.Add(new SqlRow(cx.GetUid(),cx,v));
                    }
                case Sqlx.LBRACK:
                        return (SqlValue)cx.Add(ParseSqlDocArray());
                case Sqlx.LSS:
                    return (SqlValue)cx.Add(ParseXmlValue());
            }
            // "SQLFUNCTIONS"
            Sqlx kind;
            SqlValue? val = null;
            SqlValue? op1 = null;
            SqlValue? op2 = null;
            CTree<long,bool>? filter = null;
            Sqlx mod = Sqlx.NO;
            WindowSpecification? ws = null;
            Ident? windowName = null;
            lp = LexPos();
            switch (tok)
            {
                case Sqlx.ABS:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(Domain.UnionNumeric);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.ANY: goto case Sqlx.COUNT;
                case Sqlx.AVG: goto case Sqlx.COUNT;
                case Sqlx.CARDINALITY: // multiset arg functions
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(Domain.Collection);
                        if (kind != Sqlx.MULTISET)
                            throw new DBException("42113", kind).Mix();
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.CAST:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(Domain.Content);
                        Mustbe(Sqlx.AS);
                        op1 = (SqlValue)cx.Add(new SqlTypeExpr(cx.GetUid(),ParseSqlDataType()));
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.CEIL: goto case Sqlx.ABS;
                case Sqlx.CEILING: goto case Sqlx.ABS;
                case Sqlx.CHAR_LENGTH:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(Domain.Char);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.CHARACTER_LENGTH: goto case Sqlx.CHAR_LENGTH;
                case Sqlx.COALESCE:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        op1 = ParseSqlValue(xp);
                        Mustbe(Sqlx.COMMA);
                        op2 = ParseSqlValue(xp);
                        while (tok == Sqlx.COMMA)
                        {
                            Next();
                            op1 = new SqlCoalesce(LexPos().dp, cx,op1,op2);
                            op2 = ParseSqlValue(xp);
                        }
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(new SqlCoalesce(lp.dp, cx, op1, op2));
                    }
                case Sqlx.COLLECT: goto case Sqlx.COUNT;
#if OLAP
                case Sqlx.CORR: goto case Sqlx.COVAR_POP;
#endif
                case Sqlx.COUNT: // actually a special case: but deal with all ident-arg aggregates here
                    {
                        kind = tok;
                        mod = Sqlx.NO; // harmless default value
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        if (kind == Sqlx.COUNT && tok == Sqlx.TIMES)
                        {
                            val = (SqlValue)cx.Add(new SqlLiteral(LexPos().dp,new TInt(1L))
                                +(ObInfo.Name,"*"));
                            Next();
                            mod = Sqlx.TIMES;
                        }
                        else
                        {
                            if (tok == Sqlx.ALL)
                                Next();
                            else if (tok == Sqlx.DISTINCT)
                            {
                                mod = tok;
                                Next();
                            }
                            val = ParseSqlValue(Domain.Content);
                        }
                        Mustbe(Sqlx.RPAREN);
                        if (tok == Sqlx.FILTER)
                        {
                            Next();
                            Mustbe(Sqlx.LPAREN);
                            if (tok == Sqlx.WHERE)
                                filter = ParseWhereClause();
                            Mustbe(Sqlx.RPAREN);
                        }
                        if (tok == Sqlx.OVER && wfok)
                        {
                            Next();
                            if (tok == Sqlx.ID)
                            {
                                windowName = new Ident(this);
                                Next();
                            }
                            else
                            {
                                ws = ParseWindowSpecificationDetails();
                                ws += (ObInfo.Name, "U" + DBObject.Uid(cx.GetUid()));
                            }
                        }
                        var m = BTree<long, object>.Empty;
                        if (filter != null &&  filter !=CTree<long,bool>.Empty)
                            m += (SqlFunction.Filter, filter);
                        if (ws != null)
                            m += (SqlFunction.Window, ws.defpos);
                        if (windowName!=null)
                            m += (SqlFunction.WindowId, windowName);
                        var sf = new SqlFunction(lp.dp, cx, kind, val, op1, op2, mod, m);
                        return (SqlValue)cx.Add(sf);
                    }
#if OLAP
                case Sqlx.COVAR_POP:
                    {
                        QuerySpecification se = cx as QuerySpecification;
                        if (se != null)
                            se.aggregates = true;
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        op1 = ParseSqlValue(tp);
                        Mustbe(Sqlx.COMMA);
                        op2 = ParseSqlValue(tp);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.COVAR_SAMP: goto case Sqlx.COVAR_POP;
                case Sqlx.CUME_DIST: goto case Sqlx.RANK;
#endif
                case Sqlx.CURRENT: // OF cursor --- delete positioned and update positioned
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.OF);
                        val = (SqlValue?)cx.Get(ParseIdentChain(),xp);
                        break;
                    }
#if OLAP
                case Sqlx.DENSE_RANK: goto case Sqlx.RANK;
#endif
                case Sqlx.ELEMENT: goto case Sqlx.CARDINALITY;
                case Sqlx.EVERY: goto case Sqlx.COUNT;
                case Sqlx.EXP: goto case Sqlx.ABS;
                case Sqlx.EXTRACT:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        mod = tok;
                        Mustbe(Sqlx.YEAR, Sqlx.MONTH, Sqlx.DAY, Sqlx.HOUR, Sqlx.MINUTE, Sqlx.SECOND);
                        Mustbe(Sqlx.FROM);
                        val = ParseSqlValue(Domain.UnionDate);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.FLOOR: goto case Sqlx.ABS;
                case Sqlx.FUSION: goto case Sqlx.COUNT;
                case Sqlx.GROUPING:
                    {
                        Next();
                        return (SqlValue)cx.Add(new ColumnFunction(lp.dp, ParseIDList()));
                    }
                case Sqlx.INTERSECT: goto case Sqlx.COUNT;
                case Sqlx.LN: goto case Sqlx.ABS;
                case Sqlx.LOWER: goto case Sqlx.SUBSTRING;
                case Sqlx.MAX: 
                case Sqlx.MIN:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(Domain.UnionDateNumeric);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.MOD: goto case Sqlx.NULLIF;
                case Sqlx.NULLIF:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        op1 = ParseSqlValue(Domain.Content);
                        Mustbe(Sqlx.COMMA);
                        op2 = ParseSqlValue(cx._Dom(op1)??Domain.Content);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
#if SIMILAR
                case Sqlx.OCCURRENCES_REGEX:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        var pat = ParseSqlValue(Domain.Char.defpos);
                        SqlValue flg = null;
                        if (Match(Sqlx.FLAG))
                        {
                            Next();
                            flg = ParseSqlValue(Domain.Char.defpos);
                        }
                        op1 = new RegularExpression(cx, pat, flg, null);
                        Mustbe(Sqlx.IN);
                        val = ParseSqlValue();
                        var rep = new RegularExpressionParameters(cx);
                        if (Match(Sqlx.FROM))
                        {
                            Next();
                            rep.startPos = ParseSqlValue(Domain.Int.defpos);
                        }
                        if (Match(Sqlx.CHARACTERS, Sqlx.OCTETS))
                        {
                            rep.units = tok;
                            Next();
                        }
                        op2 = rep;
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
#endif
                case Sqlx.OCTET_LENGTH: goto case Sqlx.CHAR_LENGTH;
                case Sqlx.PARTITION:
                    {
                        kind = tok;
                        Next();
                        break;
                    }
#if OLAP
                case Sqlx.PERCENT_RANK: goto case Sqlx.RANK;
                case Sqlx.PERCENTILE_CONT:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        op1 = ParseSqlValue(Domain.UnionNumeric.defpos);
                        Mustbe(Sqlx.RPAREN);
                        WindowSpecification ws = ParseWithinGroupSpecification();
                        window = ws;
                        if (ws.order == null || ws.partition == ws.order.Length)
                            throw new DBException("42128").Mix();
                        var oi = ws.order[ws.partition];
                        val = oi.what;
                        ws.name = tr.local.genuid(0);
                        break;
                    }
                case Sqlx.PERCENTILE_DISC: goto case Sqlx.PERCENTILE_CONT;
                case Sqlx.POSITION:
                    {
                        kind = tok;
                        Next();
                        if (tok == Sqlx.LPAREN)
                        {
                            Next();
                            op1 = ParseSqlValue(Domain.Int.defpos);
                            Mustbe(Sqlx.IN);
                            op2 = ParseSqlValue(Domain.Content.defpos);
                            Mustbe(Sqlx.RPAREN);
                        }
                        break;
                    }
                case Sqlx.POSITION_REGEX:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        mod = Sqlx.AFTER;
                        if (Match(Sqlx.START, Sqlx.AFTER))
                            mod = tok;
                        var pat = ParseSqlValue(Domain.Char.defpos);
                        SqlValue flg = null;
                        if (Match(Sqlx.FLAG))
                        {
                            Next();
                            flg = ParseSqlValue(Domain.Char.defpos);
                        }
                        op1 = new RegularExpression(cx, pat, flg, null);
                        Mustbe(Sqlx.IN);
                        val = ParseSqlValue(rt);
                        var rep = new RegularExpressionParameters(cx);
                        if (Match(Sqlx.WITH))
                        {
                            Next();
                            rep.with = ParseSqlValue(Domain.Int.defpos);
                        }
                        if (Match(Sqlx.FROM))
                        {
                            Next();
                            rep.startPos = ParseSqlValue(Domain.Int.defpos);
                        }
                        if (Match(Sqlx.CHARACTERS, Sqlx.OCTETS))
                        {
                            rep.units = tok;
                            Next();
                        }
                        if (Match(Sqlx.OCCURRENCE))
                        {
                            Next();
                            rep.occurrence = ParseSqlValue(Domain.Int.defpos);
                        }
                        if (Match(Sqlx.GROUP))
                        {
                            Next();
                            rep.group = ParseSqlValue(Domain.Char.defpos);
                        }
                        Mustbe(Sqlx.RPAREN);
                        op2 = rep;
                        break;
                    }
#endif
                case Sqlx.POWER: goto case Sqlx.MOD;
                case Sqlx.RANK: goto case Sqlx.ROW_NUMBER;
#if OLAP
                case Sqlx.REGR_COUNT: goto case Sqlx.COVAR_POP;
                case Sqlx.REGR_AVGX: goto case Sqlx.COVAR_POP;
                case Sqlx.REGR_AVGY: goto case Sqlx.COVAR_POP;
                case Sqlx.REGR_INTERCEPT: goto case Sqlx.COVAR_POP;
                case Sqlx.REGR_R2: goto case Sqlx.COVAR_POP;
                case Sqlx.REGR_SLOPE: goto case Sqlx.COVAR_POP;
                case Sqlx.REGR_SXX: goto case Sqlx.COVAR_POP;
                case Sqlx.REGR_SXY: goto case Sqlx.COVAR_POP;
                case Sqlx.REGR_SYY: goto case Sqlx.COVAR_POP;
#endif
                case Sqlx.ROW_NUMBER:
                    {
                        kind = tok;
                        Next();
                        lp = LexPos();
                        Mustbe(Sqlx.LPAREN);
                        if (tok == Sqlx.RPAREN)
                        {
                            Next();
                            Mustbe(Sqlx.OVER);
                            if (tok == Sqlx.ID)
                            {
                                windowName = new Ident(this);
                                Next();
                            }
                            else
                            {
                                ws = ParseWindowSpecificationDetails();
                                ws+=(ObInfo.Name,"U"+ cx.db.uid);
                            }
                            var m = BTree<long, object>.Empty;
                            if (filter != null)
                                m += (SqlFunction.Filter, filter);
                            if (ws != null)
                                m += (SqlFunction.Window, ws.defpos);
                            if (windowName != null)
                                m += (SqlFunction.WindowId, windowName);
                            return (SqlValue)cx.Add(new SqlFunction(lp.dp, cx, kind, val, op1, op2, mod, m));
                        }
                        var v = new BList<long?>(cx.Add(ParseSqlValue(xp)).defpos);
                        for (var i=1; tok == Sqlx.COMMA;i++)
                        {
                            Next();
                            v += ParseSqlValue(xp).defpos;
                        }
                        Mustbe(Sqlx.RPAREN);
                        val = new SqlRow(LexPos().dp, cx, xp, v);
                        var f = new SqlFunction(lp.dp, cx, kind, val, op1, op2, mod, BTree<long, object>.Empty
                            + (SqlFunction.Window, ParseWithinGroupSpecification().defpos)
                            + (SqlFunction.WindowId,"U"+ cx.db.uid));
                        return (SqlValue)cx.Add(f);
                    }
                case Sqlx.ROWS: // Pyrrho (what is this?)
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        if (Match(Sqlx.TIMES))
                        {
                            mod = Sqlx.TIMES;
                            Next();
                        }
                        else
                            val = ParseSqlValue(Domain.Int);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.SET: goto case Sqlx.CARDINALITY;
                case Sqlx.SOME: goto case Sqlx.COUNT;
                case Sqlx.DESCRIBE:
                case Sqlx.SPECIFICTYPE:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(new SqlFunction(lp.dp, cx, kind, null, null, null, Sqlx.NO));
                    }
                case Sqlx.SQRT: goto case Sqlx.ABS;
                case Sqlx.STDDEV_POP: goto case Sqlx.COUNT;
                case Sqlx.STDDEV_SAMP: goto case Sqlx.COUNT;
                case Sqlx.SUBSTRING:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(Domain.Char);
                        if (kind == Sqlx.SUBSTRING)
                        {
#if SIMILAR
                            if (tok == Sqlx.SIMILAR)
                            {
                                mod = Sqlx.REGULAR_EXPRESSION;
                                Next();
                                var re = ParseSqlValue();
                                Mustbe(Sqlx.ESCAPE);
                                op1 = new RegularExpression(cx, re, null, ParseSqlValue());
                            }
                            else
#endif
                            {
                                Mustbe(Sqlx.FROM);
                                op1 = ParseSqlValue(Domain.Int);
                                if (tok == Sqlx.FOR)
                                {
                                    Next();
                                    op2 = ParseSqlValue(Domain.Int);
                                }
                            }
                        }
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
#if SIMILAR
                case Sqlx.SUBSTRING_REGEX:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        var pat = ParseSqlValue(Domain.Char.defpos);
                        SqlValue flg = null;
                        if (Match(Sqlx.FLAG))
                        {
                            Next();
                            flg = ParseSqlValue(Domain.Char.defpos);
                        }
                        op1 = new RegularExpression(cx, pat, flg, null);
                        Mustbe(Sqlx.IN);
                        val = ParseSqlValue(Domain.Char.defpos);
                        var rep = new RegularExpressionParameters(cx);
                        if (Match(Sqlx.FROM))
                        {
                            Next();
                            rep.startPos = ParseSqlValue(Domain.Int.defpos);
                        }
                        if (Match(Sqlx.CHARACTERS,Sqlx.OCTETS))
                        {
                            rep.units = tok;
                            Next();
                        }
                        if (Match(Sqlx.OCCURRENCE))
                        {
                            Next();
                            rep.occurrence = ParseSqlValue(Domain.Int.defpos);
                        }
                        if (Match(Sqlx.GROUP))
                        {
                            Next();
                            rep.group = ParseSqlValue(Domain.Int.defpos);
                        }
                        op2 = rep;
                        break;
                    }
#endif
                case Sqlx.SUM: goto case Sqlx.COUNT;
#if SIMILAR
                case Sqlx.TRANSLATE_REGEX:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        var pat = ParseSqlValue();
                        SqlValue flg = null;
                        if (Match(Sqlx.FLAG))
                        {
                            Next();
                            flg = ParseSqlValue(Domain.Char.defpos);
                        }
                        op1 = new RegularExpression(cx, pat, flg, null);
                        Mustbe(Sqlx.IN);
                        val = ParseSqlValue(t);
                        var rep = new RegularExpressionParameters(cx);
                        if (Match(Sqlx.WITH))
                        {
                            Next();
                            rep.with = ParseSqlValueDomain.Char.defpos();
                        }
                        if (Match(Sqlx.FROM))
                        {
                            Next();
                            rep.startPos = ParseSqlValue(Domain.Int.defpos);
                        }
                        if (Match(Sqlx.OCCURRENCE))
                        {
                            Next();
                            if (tok == Sqlx.ALL)
                            {
                                Next();
                                rep.all = true;
                            }
                            else
                                rep.occurrence = ParseSqlValue(Domain.Int.defpos);
                        }
                        op2 = rep;
                        break;
                    }
#endif
                case Sqlx.TRIM:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        if (Match(Sqlx.LEADING, Sqlx.TRAILING, Sqlx.BOTH))
                        {
                            mod = tok;
                            Next();
                        }
                        val = ParseSqlValue(Domain.Char);
                        if (tok == Sqlx.FROM)
                        {
                            Next();
                            op1 = val; // trim character
                            val = ParseSqlValue(Domain.Char);
                        }
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.UPPER: goto case Sqlx.SUBSTRING;
#if OLAP
                case Sqlx.VAR_POP: goto case Sqlx.COUNT;
                case Sqlx.VAR_SAMP: goto case Sqlx.COUNT;
#endif
                case Sqlx.VERSIONING:
                    kind = tok;
                    Next();
                    break;
                case Sqlx.XMLAGG: goto case Sqlx.COUNT;
                case Sqlx.XMLCAST: goto case Sqlx.CAST;
                case Sqlx.XMLCOMMENT:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(Domain.Char);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.XMLCONCAT:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(Domain.Char);
                        while (tok == Sqlx.COMMA)
                        {
                            Next();
                            val = (SqlValue)cx.Add(new SqlValueExpr(LexPos().dp, cx, Sqlx.XMLCONCAT, 
                                val, ParseSqlValue(Domain.Char), Sqlx.NO));
                        }
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(val);
                    }
                case Sqlx.XMLELEMENT:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        Mustbe(Sqlx.NAME);
                        var name = lxr.val;
                        Mustbe(Sqlx.ID);
                        bool namespaces = false, attributes = false;
                        int n = 0;
                        while (tok == Sqlx.COMMA)
                        {
                            Next();
                            var llp = LexPos();
                            if (n == 0 && (!namespaces) && (!attributes) && tok == Sqlx.XMLNAMESPACES)
                            {
                                Next();
                                ParseXmlNamespaces();
                                namespaces = true;
                            }
                            else if (n == 0 && (!attributes) && tok == Sqlx.XMLATTRIBUTES)
                            {
                                Next();
                                Mustbe(Sqlx.LPAREN);
                                var doc = new SqlRow(llp.dp,BTree<long, object>.Empty);
                                var v = ParseSqlValue(Domain.Char);
                                var j = 0;
                                var a = new Ident("Att"+(++j), cx.Ix(0));
                                if (tok == Sqlx.AS)
                                {
                                    Next();
                                    a = new Ident(this);
                                    Mustbe(Sqlx.ID);
                                }
                                doc += (cx,v+(ObInfo.Name,a.ident));
                                a = new Ident("Att" + (++j), cx.Ix(0));
                                while (tok == Sqlx.COMMA)
                                {
                                    Next();
                                    var w = ParseSqlValue(Domain.Char);
                                    if (tok == Sqlx.AS)
                                    {
                                        Next();
                                        a = new Ident(this);
                                        Mustbe(Sqlx.ID);
                                    }
                                }
                                doc += (cx,v + (ObInfo.Name, a.ident));
                                v = (SqlValue)cx.Add(new SqlValueExpr(lp.dp, cx, Sqlx.XMLATTRIBUTES, v, null, Sqlx.NO));
                                Mustbe(Sqlx.RPAREN);
                                op2 = v;
                            }
                            else
                            {
                                val = (SqlValue)cx.Add(new SqlValueExpr(lp.dp, cx, Sqlx.XML, val, 
                                    ParseSqlValue(Domain.Char), Sqlx.NO));
                                n++;
                            }
                        }
                        Mustbe(Sqlx.RPAREN);
                        op1 = (SqlValue)cx.Add(new SqlLiteral(LexPos().dp,name));
                        break;
                    }
                case Sqlx.XMLFOREST:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        if (tok == Sqlx.XMLNAMESPACES)
                        {
                            Next();
                            ParseXmlNamespaces();
                        }
                        val = ParseSqlValue(Domain.Char);
                        while (tok == Sqlx.COMMA)
                        {
                            var llp = LexPos();
                            Next();
                            val = (SqlValue)cx.Add(new SqlValueExpr(llp.dp, cx, Sqlx.XMLCONCAT, val, 
                                ParseSqlValue(Domain.Char), Sqlx.NO));
                        }
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(val);
                    }
                case Sqlx.XMLPARSE:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        Mustbe(Sqlx.CONTENT);
                        val = ParseSqlValue(Domain.Char);
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(val);
                    }
                case Sqlx.XMLQUERY:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        op1 = ParseSqlValue(Domain.Char);
                        if (tok != Sqlx.COMMA)
                            throw new DBException("42000", tok).Mix();
                        lxr.XmlNext(')');
                        op2 = (SqlValue)cx.Add(new SqlLiteral(LexPos().dp, new TChar(lxr.val.ToString())));
                        Next();
                        break;
                    }
                case Sqlx.XMLPI:
                    {
                        kind = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        Mustbe(Sqlx.NAME);
                        val = (SqlValue)cx.Add(new SqlLiteral(LexPos().dp, new TChar( lxr.val.ToString())));
                        Mustbe(Sqlx.ID);
                        if (tok == Sqlx.COMMA)
                        {
                            Next();
                            op1 = ParseSqlValue(Domain.Char);
                        }
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }

                default:
                    return (SqlValue)cx.Add(new SqlProcedureCall(lp.dp,
                        (CallStatement)ParseProcedureCall(xp)));
            }
            return (SqlValue)cx.Add(new SqlFunction(lp.dp, cx, kind, val, op1, op2, mod));
        }

        /// <summary>
        /// WithinGroup = WITHIN GROUP '(' OrderByClause ')' .
        /// </summary>
        /// <returns>A WindowSpecification</returns>
        WindowSpecification ParseWithinGroupSpecification()
        {
            WindowSpecification r = new(LexDp());
            Mustbe(Sqlx.WITHIN);
            Mustbe(Sqlx.GROUP);
            Mustbe(Sqlx.LPAREN);
            if (cx._Dom(r.order) is Domain od)
                r+=(cx,WindowSpecification.Order,ParseOrderClause(od,false));
            Mustbe(Sqlx.RPAREN);
            return r;
        }
        /// <summary>
		/// XMLOption = WITH XMLNAMESPACES '(' XMLNDec {',' XMLNDec } ')' .
        /// </summary>
        void ParseXmlOption(bool donewith)
		{
            if (!donewith)
            {
                if (tok != Sqlx.WITH)
                    return;
                Next();
            }
			Mustbe(Sqlx.XMLNAMESPACES);
			ParseXmlNamespaces();
		}
        /// <summary>
		/// XMLNDec = (string AS id) | (DEFAULT string) | (NO DEFAULT) .
        /// </summary>
        /// <returns>the executable</returns>
        Executable ParseXmlNamespaces()
		{
            var pn = new XmlNameSpaces(LexDp());
			Mustbe(Sqlx.LPAREN);
			if (tok==Sqlx.NO)
			{
				Next();
				Mustbe(Sqlx.DEFAULT);
			} 
			else if (tok==Sqlx.DEFAULT)
			{
				Next();
				var o = lxr.val;
				Mustbe(Sqlx.CHARLITERAL);
                pn += (XmlNameSpaces.Nsps,pn.nsps+("", o.ToString()));
			}
			else
				for (Sqlx sep = Sqlx.COMMA;sep==Sqlx.COMMA;sep=tok)
				{
					var s = lxr.val;
					Mustbe(Sqlx.CHARLITERAL);
					Mustbe(Sqlx.AS);
					var p = lxr.val;
					Mustbe(Sqlx.ID);
					pn +=(XmlNameSpaces.Nsps,pn.nsps+(s.ToString(),p.ToString()));
				}
			Mustbe(Sqlx.RPAREN);
            return (Executable)cx.Add(pn);
		}
        /// <summary>
		/// WindowDetails = [Window_id] [ PartitionClause] [ OrderByClause ] [ WindowFrame ] .
		/// PartitionClause =  PARTITION BY  OrdinaryGroup .
		/// WindowFrame = (ROWS|RANGE) (WindowStart|WindowBetween) [ Exclusion ] .
		/// WindowStart = ((TypedValue | UNBOUNDED) PRECEDING) | (CURRENT ROW) .
		/// WindowBetween = BETWEEN WindowBound AND WindowBound .
        /// </summary>
        /// <returns>The WindowSpecification</returns>
		WindowSpecification ParseWindowSpecificationDetails()
		{
			Mustbe(Sqlx.LPAREN);
			WindowSpecification w = new(LexDp());
            if (tok == Sqlx.ID)
            {
                w+=(WindowSpecification.OrderWindow,lxr.val.ToString());
                Next();
            }
            var dm = Domain.Row;
            if (tok==Sqlx.PARTITION)
			{
				Next();
				Mustbe(Sqlx.BY);
                var rs = dm.representation;
                var rt = dm.rowType;
                for (var b = ParseSqlValueList(Domain.Content).First(); b != null; b = b.Next())
                    if (b.value() is long p && cx._Dom(p) is Domain dp) {
                        rt += p; rs += (p, dp);
                    }
                dm = (Domain)dm.New(cx,dm.mem+(Domain.RowType,rt)+(Domain.Representation,rs));
                w += (cx, WindowSpecification.PartitionType, dm.defpos);
			}
            if (tok == Sqlx.ORDER)
            {
                var oi = ParseOrderClause(dm);
                oi = (Domain)cx.Add(oi);
                w += (cx, WindowSpecification.Order, oi.defpos);
            }
			if (Match(Sqlx.ROWS,Sqlx.RANGE))
			{
				w+=(WindowSpecification.Units,tok);
				Next();
                if (tok == Sqlx.BETWEEN)
                {
                    Next();
                    w+=(WindowSpecification.Low,ParseWindowBound());
                    Mustbe(Sqlx.AND);
                    w+=(WindowSpecification.High,ParseWindowBound());
                }
                else
                    w += (WindowSpecification.Low, ParseWindowBound());
                if (Match(Sqlx.EXCLUDE))
                {
                    Next();
                    if (Match(Sqlx.CURRENT))
                    {
                        w+=(WindowSpecification.Exclude,tok);
                        Next();
                        Mustbe(Sqlx.ROW);
                    }
                    else if (Match(Sqlx.TIES))
                    {
                        w += (WindowSpecification.Exclude, Sqlx.EQL);
                        Next();
                    }
                    else if (Match(Sqlx.NO))
                    {
                        Next();
                        Mustbe(Sqlx.OTHERS);
                    }
                    else
                    {
                        w += (WindowSpecification.Exclude, tok);
                        Mustbe(Sqlx.GROUP);
                    }
                }
			}
			Mustbe(Sqlx.RPAREN);
            cx.Add(w);
			return w;
		}
        /// <summary>
		/// WindowBound = WindowStart | ((TypedValue | UNBOUNDED) FOLLOWING ) .
        /// </summary>
        /// <returns>The WindowBound</returns>
        WindowBound ParseWindowBound()
        {
            bool prec = false,unbd = true;
            TypedValue d = TNull.Value;
            if (Match(Sqlx.CURRENT))
            {
                Next();
                Mustbe(Sqlx.ROW);
                return new WindowBound();
            }
            if (Match(Sqlx.UNBOUNDED))
                Next();
            else if (tok == Sqlx.INTERVAL)
            {
                Next();
                var o=lxr.val;
                var lp = LexPos();
                Mustbe(Sqlx.CHAR);
                Domain di = ParseIntervalType();
                d = di.Parse(new Scanner(lp.dp,o.ToString().ToCharArray(),0,cx));
                unbd = false;
            }
            else
            {
                d = lxr.val;
                Mustbe(Sqlx.INTEGERLITERAL, Sqlx.NUMERICLITERAL);
                unbd = false;
            }
            if (Match(Sqlx.PRECEDING))
            {
                Next();
                prec = true;
            }
            else
                Mustbe(Sqlx.FOLLOWING);
            if (unbd)
                return new WindowBound()+(WindowBound.Preceding,prec);
            return new WindowBound()+(WindowBound.Preceding,prec)+(WindowBound.Distance,d);
        }
        /// <summary>
        /// For the REST service, we can have a value, maybe a procedure call:
        /// </summary>
        /// <param name="sql">an expression string to parse</param>
        /// <returns>the SqlValue</returns>
        internal SqlValue ParseSqlValueItem(string sql,Domain xp)
        {
            lxr = new Lexer(cx,sql);
            tok = lxr.tok;
            if (LexDp() > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length); // not really needed here
            return ParseSqlValueItem(xp,false);
        }
        /// <summary>
        /// For the REST service there may be an explicit procedure call
        /// </summary>
        /// <param name="sql">a call statement to parse</param>
        /// <returns>the CallStatement</returns>
        internal CallStatement ParseProcedureCall(string sql,Domain xp)
        {
            lxr = new Lexer(cx,sql); 
            tok = lxr.tok;
            if (LexDp() > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId +sql.Length); // not really needed here
            var n = new Ident(this);
            Mustbe(Sqlx.ID);
            var ps = BList<long?>.Empty;
            var lp = LexPos();
            if (tok == Sqlx.LPAREN)
            {
                Next();
                ps = ParseSqlValueList(Domain.Content);
            }
            var arity = cx.Signature(ps);
            Mustbe(Sqlx.RPAREN);
            var pp = cx.role.procedures[n.ident]?[arity] ?? -1;
            var pr = cx.db.objects[pp] as Procedure
                ?? throw new DBException("42108", n).Mix();
            var fc = new CallStatement(lp.dp, cx, pr, n.ident, ps);
            return (CallStatement)cx.Add(fc);
        }
        /// <summary>
		/// UserFunctionCall = Id '(' [  TypedValue {','  TypedValue}] ')' .
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <returns>the Executable</returns>
        Executable ParseProcedureCall(Domain xp)
        {
            var id = new Ident(this);
            Mustbe(Sqlx.ID);
            var lp = LexPos();
            Mustbe(Sqlx.LPAREN);
            var ps = ParseSqlValueList(Domain.Content);
            var a = cx.Signature(ps);
            Mustbe(Sqlx.RPAREN);
            if (cx.role.procedures[id.ident]?[a] is not long pp || 
                cx.db.objects[pp] is not Procedure pr)
                throw new DBException("42108", id.ident).Mix();
            var fc = new CallStatement(lp.dp, cx, pr, id.ident, ps);
            return (Executable)cx.Add(fc);
        }
        /// <summary>
        /// Parse a list of Sql values
        /// </summary>
        /// <param name="t">the expected obs type</param>
        /// <returns>the List of SqlValue</returns>
        BList<long?> ParseSqlValueList(Domain xp)
        {
            var r = BList<long?>.Empty;
            Domain ei;
            switch (xp.kind)
            {
                case Sqlx.ARRAY:
                case Sqlx.MULTISET:
                    ei = xp.elType;
                    break;
                case Sqlx.CONTENT:
                    for (; ; )
                    {
                        var v = ParseSqlValue(xp);
                        cx.Add(v);
                        r += v.defpos;
                        if (tok == Sqlx.COMMA)
                            Next();
                        else break;
                    }
                    return r;
                default:
                    ei = xp;
                    break;
            }
            for (; ; )
            {
                var v = (ei.Length>0)?
                    ParseSqlRow(ei) :
                    ParseSqlValue(ei);
                cx.Add(v);
                if (tok == Sqlx.AS)
                {
                    Next();
                    var d = ParseSqlDataType();
                    v = new SqlTreatExpr(LexPos().dp, v, d); //.Needs(v);
                    cx.Add(v);
                }
                r += v.defpos;
                if (tok == Sqlx.COMMA)
                    Next();
                else
                    break;
            }
            return r;
        }
        public SqlRow ParseSqlRow(Domain xp)
        {
            var llp = LexPos();
            Mustbe(Sqlx.LPAREN);
            var lk = BList<long?>.Empty;
            var i = 0;
            for (var b = xp.rowType.First(); b != null && i < xp.display; b = b.Next(), i++)
                if (b.value() is long p && xp.representation[p] is Domain dt)
                {
                    if (i > 0)
                        Mustbe(Sqlx.COMMA);
                    var v = ParseSqlValue(dt);
                    cx.Add(v);
                    lk += v.defpos;
                }
            Mustbe(Sqlx.RPAREN);
            return (SqlRow)cx.Add(new SqlRow(llp.dp, cx, xp, lk));
        }
        /// <summary>
        /// Parse an SqlRow
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="sql">The string to parse</param>
        /// <param name="result">the expected obs type</param>
        /// <returns>the SqlRow</returns>
        public SqlValue ParseSqlValueList(string sql,Domain xp)
        {
            lxr = new Lexer(cx,sql);
            tok = lxr.tok;
            if (tok == Sqlx.LPAREN)
                return ParseSqlRow(xp);         
            return ParseSqlValueEntry(xp,false);
        }
        /// <summary>
        /// Get a document item
        /// </summary>
        /// <param name="v">The document being constructed</param>
        (string,SqlValue) GetDocItem(Context cx)
        {
            Ident k = new(this);
            Mustbe(Sqlx.ID);
            Mustbe(Sqlx.COLON);
            return (k.ident,(SqlValue)cx.Add(ParseSqlValue(Domain.Content)));
        }
        CTree<string,TypedValue> GetDocItem(CTree<string,TypedValue>c)
        {
            var k = lxr.val;
            Mustbe(Sqlx.ID);
            lxr.tok = Sqlx.A; // replaced Mustbe(Sqlx.COLON) with these two lines to get a special TGParam
            Next();
            var v = lxr.val;
            Next();
            return c + (k.ToString(), v);
        }
        /// <summary>
        /// Parse a document array
        /// </summary>
        /// <returns>the SqlDocArray</returns>
        public SqlValue ParseSqlDocArray()
        {
            var v = new SqlRowArray(LexPos().dp,BTree<long, object>.Empty);
            cx.Add(v);
            Next();
            if (tok != Sqlx.RBRACK)
                v += ParseSqlRow(Domain.Content);
            while (tok == Sqlx.COMMA)
            {
                Next();
                v+=ParseSqlRow(Domain.Content);
            }
            Mustbe(Sqlx.RBRACK);
            return (SqlValue)cx.Add(v);
        }
        /// <summary>
        /// Parse an XML value
        /// </summary>
        /// <returns>the SqlValue</returns>
        public SqlValue ParseXmlValue()
        {
            Mustbe(Sqlx.LSS);
            var e = GetName();
            var v = new SqlXmlValue(LexPos().dp,e,SqlNull.Value,BTree<long, object>.Empty);
            cx.Add(v);
            while (tok!=Sqlx.GTR && tok!=Sqlx.DIVIDE)
            {
                var a = GetName();
                v+=(SqlXmlValue.Attrs,v.attrs+(a, cx.Add(ParseSqlValue(Domain.Char)).defpos));
            }
            if (tok != Sqlx.DIVIDE)
            {
                Next(); // GTR
                if (tok == Sqlx.ID)
                {
                    var st = lxr.start;
                    Next();
                    if (tok == Sqlx.ID || tok != Sqlx.LSS)
                    {
                        while (tok != Sqlx.LSS)
                            Next();
                        v+=(SqlXmlValue.Content,new SqlLiteral(LexPos().dp,
                            new TChar(new string(lxr.input, st, lxr.start - st))));
                    }
                    else
                    {
                        lxr.PushBack(Sqlx.ANY);
                        lxr.pos = lxr.start;
                        v += (SqlXmlValue.Content, ParseSqlValueItem(Domain.Char, false));
                    }
                }
                else
                    while (tok != Sqlx.DIVIDE) // tok should Sqlx.LSS
                        v+=(SqlXmlValue.Children,v.children+(cx.Add(ParseXmlValue()).defpos));
                Mustbe(Sqlx.DIVIDE);
                var ee = GetName();
                if (e.prefix != ee.prefix || e.keyname != ee.keyname)
                    throw new DBException("2200N", ee).ISO();
            }
            Mustbe(Sqlx.GTR);
            return (SqlValue)cx.Add(v);
        }
        /// <summary>
        /// Parse an XML name
        /// </summary>
        /// <returns>the XmlName</returns>
        public SqlXmlValue.XmlName GetName()
        {
            var e = new SqlXmlValue.XmlName(new string(lxr.input, lxr.start, lxr.pos - lxr.start));
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.COLON)
            {
                Next();
                e=new SqlXmlValue.XmlName(new string(lxr.input, lxr.start, lxr.pos - lxr.start),
                    e.keyname);
                Mustbe(Sqlx.ID);
            }
            return e;
        }
        static CTree<long, bool> _Deps(BList<long?> bl)
        {
            var r = CTree<long, bool>.Empty;
            for (var b = bl.First(); b != null; b = b.Next())
                if (b.value() is long p)
                    r += (p, true);
            return r;
        }
    }
}