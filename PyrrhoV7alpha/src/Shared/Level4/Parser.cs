using System;
using System.Collections.Generic;
using System.Globalization;
using Pyrrho.Common;
using Pyrrho.Level2;
using Pyrrho.Level3;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2020
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
    /// and can be distinguished by the fact that they create a new Scanner()
    /// 
    /// Most of the grammar below comes from SQL2003
    /// the comments are extracts from Pyrrho.doc synopsis of syntax
    /// 
    /// Many SQL statements parse to Executables (can be placed in stored procedures etc)
    /// Can then be executed imediately depending on parser settings.
    /// 
    /// Some constructs get parsed during database Load(): these should never try to change the schema
    /// or make other changes. db.parse should only be Obey within a transaction.
    /// This means that (for now at least) stored executable code (triggers, procedures) should
    /// never attempt schema changes. 
    /// </summary>
	internal class Parser
    {
        /// <summary>
        /// Both tr and cx have their idea of role. Use tr.role to get information
        /// about Tables and cx.role to store and get information about TabelReferences
        /// </summary>
        public Context cx; // updatable: has volatile query rowTypes.
        /// <summary>
        /// The lexer breaks the input into tokens for us. During parsing
        /// lxr.val is the object corresponding to the current token,
        /// lxr.start and lxr.pos delimit the current token
        /// </summary>
		Lexer lxr;
        /// <summary>
        /// The current token
        /// </summary>
		Sqlx tok;
        /// <summary>
        /// The constructor
        /// </summary>
        /// <param name="t">The transaction</param>
        public Parser(Database da)
        {
            cx = new Context(da);
            cx.db = da.Transact(da.nextId, da.source);
        }
        public Parser(Context c)
        {
            cx = c;
        }
        /// <summary>
        /// Create a Parser for Constraint definition
        /// </summary>
        /// <param name="_cx"></param>
        /// <param name="src"></param>
        /// <param name="infos"></param>
        public Parser(Context _cx,Ident src,BTree<long,ObInfo> infos=null)
        {
            cx = _cx.ForConstraintParse(infos);
            cx.db = cx.db+(Database._ExecuteStatus,ExecuteStatus.Parse);
            lxr = new Lexer(src);
            tok = lxr.tok;
        }
        /// <summary>
        /// Create a Parser for Constraint definition
        /// </summary>
        /// <param name="rdr"></param>
        /// <param name="scr"></param>
        /// <param name="tb"></param>
        public Parser(Reader rdr, Ident scr, DBObject ob) : this(rdr.context, scr, _Infs(rdr,ob)) 
        { }
        static BTree<long,ObInfo> _Infs(Reader rdr,DBObject ob)
        {
            var infs = BTree<long, ObInfo>.Empty;
            var cs = RowType.Empty;
            for (var b=ob.domain.representation.First();b!=null;b=b.Next())
                cs += (b.key(),b.value());
            var ti = new ObInfo(ob.defpos, rdr.names[ob.defpos], ob.kind, ob.domain, cs);
            infs += (ob.defpos, ti);
            for (var b = ob.domain.representation.First(); b != null; b = b.Next())
            {
                var co = (TableColumn)rdr.context.db.objects[b.key()];
                infs += (co.defpos, new ObInfo(co.defpos, rdr.names[co.defpos], 
                    Sqlx.COLUMN, b.value()));
            }
            return infs;
        }
        /// <summary>
        /// Move to the next token
        /// </summary>
        /// <returns></returns>
		Sqlx Next()
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
		Sqlx Mustbe(params Sqlx[] t)
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
        /// The type of database maodification that may occur is determined by tr.parse.
        /// </summary>
        /// <param name="sql">the input</param>
        /// <param name="rt">the desired result type (default is Domain.Null)</param>
        /// <returns>The modified Database and the new uid highwatermark </returns>
        public Database ParseSql(string sql,long xp)
        {
            lxr = new Lexer(sql,cx.db.lexeroffset);
            tok = lxr.tok;
            var e = ParseSqlStatement(xp);
            //      cx.result = e.defpos;
            if (e.type == Executable.Type.Commit)
                return cx.db.Commit(cx);
            if (tok == Sqlx.SEMICOLON)
                Next();
            if (tok != Sqlx.EOF)
            {
                string ctx = new string(lxr.input, lxr.start, lxr.pos - lxr.start);
                throw new DBException("42000", ctx).ISO();
            }
            if (lxr.Position>Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId,cx.db.nextId+sql.Length);
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
            cx.nextHeap = pre.defpos;
            lxr = new Lexer(s, cx.db.lexeroffset);
            tok = lxr.tok;
            var b = pre.qMarks.First();
            for (;b!=null && tok!=Sqlx.EOF;b=b.Next())
            {
                var v = lxr.val;
                var lp = lxr.Position;
                if (Match(Sqlx.DATE, Sqlx.TIME, Sqlx.TIMESTAMP, Sqlx.INTERVAL))
                {
                    Sqlx tk = tok;
                    Next();
                    if (tok == Sqlx.CHARLITERAL)
                    {
                        Next();
                        v = new SqlDateTimeLiteral(lp,cx,
                            new Domain(lp, tk, BTree<long, object>.Empty), v.ToString()).val;
                    }
                }
                else
                    Mustbe(Sqlx.BLOBLITERAL, Sqlx.NUMERICLITERAL, Sqlx.REALLITERAL,
                        Sqlx.CHARLITERAL, Sqlx.INTEGERLITERAL, Sqlx.DOCUMENTLITERAL);
                cx.values += (b.value().defpos, v);
                Mustbe(Sqlx.SEMICOLON);
            }
            if (!(b == null && tok == Sqlx.EOF))
                throw new DBException("33001");
            cx = pre.target.Obey(cx);
            return cx.db;
        }
        Domain ScalarXp(long xp)
        {
            var r = cx.Dom(xp);
            switch (r.prim)
            {
                case Sqlx.ROW:
                case Sqlx.TABLE:
                    return Domain.Content;
            }
            return r;
        }
        /// <summary>
        ///SqlStatement = 	Rdf
        ///     |	Alter
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
        public Executable ParseSqlStatement(long xp)
        {
            Executable e = null;
            //            Match(Sqlx.RDF);
            switch (tok)
            {
                case Sqlx.ALTER: e = ParseAlter(); break;
                case Sqlx.CALL: e = ParseCallStatement(xp); break;
                case Sqlx.COMMIT: e = ParseCommit();  break;
                case Sqlx.CREATE: e = ParseCreateClause(); break;
                case Sqlx.DELETE: e = ParseSqlDelete(); break;
                case Sqlx.DROP: e = ParseDropStatement(); break;
                case Sqlx.GRANT: e = ParseGrant(); break;
                case Sqlx.HTTP: e = ParseHttpREST(xp); break;
                case Sqlx.INSERT: (cx,e) = ParseSqlInsert(xp); break;
                case Sqlx.REVOKE: e = ParseRevoke(); break;
                case Sqlx.ROLLBACK:
                    Next();
                    if (Match(Sqlx.WORK))
                        Next();
                    cx.db = cx.tr.Rollback(new DBException("0000").ISO());
                    break;
                case Sqlx.SELECT: e = ParseCursorSpecification(-1,xp); break;
                case Sqlx.SET: e = ParseSqlSet(); break;
                case Sqlx.TABLE: e = ParseCursorSpecification(-1,xp); break;
                case Sqlx.UPDATE: (cx,e) = ParseSqlUpdate(); break;
                case Sqlx.VALUES: e = ParseCursorSpecification(-1,xp); break;
                //    case Sqlx.WITH: e = ParseCursorSpecification(); break;
                case Sqlx.EOF: return null; // whole input is a comment
                default:
                    object ob = lxr.val;
                    if (ob == null || ((TypedValue)ob).IsNull)
                        ob = new string(lxr.input, lxr.start, lxr.pos - lxr.start);
                    throw new DBException("42000", ob).ISO();
            }
            return cx.exec = (Executable)cx.Add(e);
        }
        Executable ParseCommit()
        {
            Next();
            if (tok == Sqlx.WORK)
                Next();
            return new CommitStatement(lxr.Position);
        }
        private Executable ParseHttpREST(long xp)
        {
            Sqlx t = Next();
            SqlValue us = null;
            SqlValue pw = null;
            if (!Match(Sqlx.ADD, Sqlx.UPDATE, Sqlx.DELETE))
            {
                us = ParseSqlValue(xp,Domain.Char.defpos);
                if (Match(Sqlx.COLON))
                {
                    Next();
                    pw = ParseSqlValue(xp,Domain.Char.defpos);
                }
            }
            HttpREST h = null;
            var lp = lxr.Position;
            switch (t)
            {
                case Sqlx.ADD: h = new HttpREST(lp,"POST", us, pw); break;
                case Sqlx.UPDATE: h = new HttpREST(lp, "UPDATE", us, pw); break;
                case Sqlx.DELETE: h = new HttpREST(lp,"DELETE", us, pw); break;
            }
                Next();
                h+= (HttpREST.Url,ParseSqlValue(xp,Domain.Char.defpos).Eval(cx));
            cx = new Context(cx, h);
            try
            { 
                if (t != Sqlx.DELETE)
                {
                    var e = ParseProcedureStatement(xp);
                    cx=cx.next;
                    return (Executable)cx.Add(e);
                }
                h +=(HttpREST.Mime,"text/xml");
                if (tok == Sqlx.AS)
                {
                    Next();
                    h += (HttpREST.Mime,lxr.val.ToString());
                    Mustbe(Sqlx.CHARLITERAL);
                }
                if (Match(Sqlx.WHERE))
                {
                    Next();
                    h+=(HttpREST.Where, ParseSqlValue(xp,Domain.Bool.defpos));
                }
            }
            catch (Exception e) { throw e; }
            finally { cx = cx.next; }
            return (Executable)cx.Add(h);
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
                while (tok==Sqlx.ID)
                {
                    gps +=(((TChar)lxr.val).value, true);
                    Next();
                }
            }
            var rfs = BTree<string, bool>.Empty;
            if (tok == Sqlx.REFERENCES)
            {
                Next();
                while (tok == Sqlx.ID)
                {
                    rfs +=(((TChar)lxr.val).value, true);
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
        /// <returns>the executable type</returns>
        public Executable ParseGrant()
        {
            Executable e = null;
            Next();
            if (Match(Sqlx.SECURITY))
            {
                Next();
                var lv = MustBeLevel();
                Mustbe(Sqlx.TO);
                var nm = lxr.val;
                Mustbe(Sqlx.ID);
                var usr = cx.db.GetObject(nm.ToString()) as User ??
                    throw new DBException("42135", nm.ToString());
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.Add(new Clearance(usr.defpos, lv, tr.nextPos,cx));
                return e; // still null
            }
            else if (Match(Sqlx.PASSWORD))
            {
                TypedValue pwd = new TChar("");
                Role irole = null;
                Next();
                if (!Match(Sqlx.FOR) && !Match(Sqlx.TO))
                {
                    pwd = lxr.val;
                    Next();
                }
                if (Match(Sqlx.FOR))
                {
                    Next();
                    var rid = new Ident(lxr,Sqlx.ROLE);
                    Mustbe(Sqlx.ID);
                    irole = cx.db.GetObject(rid.ident) as Role??
                        throw new DBException("42135", rid);
                }
                Mustbe(Sqlx.TO);
                var grantees = ParseGranteeList(null);
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                {
                    var irolepos = -1L;
                    if (irole != null)
                    {
                        cx.db = tr.AccessRole(cx,true, new string[] { irole.name }, grantees, false);
                        irolepos = irole.defpos;
                    }
                    for (var i = 0; i < grantees.Length; i++)
                    {
                        var us = grantees[i];
                        cx.Add(new Authenticate(us.defpos, pwd.ToString(), irolepos,tr.nextPos, cx));
                    }
                }
                return e;// e.SPop(lxr); // e is still null of course
            }
            Match(Sqlx.OWNER, Sqlx.USAGE);
            if (Match(Sqlx.ALL, Sqlx.SELECT, Sqlx.INSERT, Sqlx.DELETE, Sqlx.UPDATE, Sqlx.REFERENCES, Sqlx.OWNER, Sqlx.TRIGGER, Sqlx.USAGE, Sqlx.EXECUTE))
            {
                e = new Executable(lxr.Position, Executable.Type.Grant);
                var priv = ParsePrivileges();
                Mustbe(Sqlx.ON);
                var (ob,_) = ParseObjectName();
                long pob = ob?.defpos ?? 0;
                Mustbe(Sqlx.TO);
                var grantees = ParseGranteeList(priv);
                bool opt = ParseGrantOption();
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.db= tr.AccessObject(cx,true, priv, pob, grantees, opt);
            }
            else
            {
                e = new Executable(lxr.Position, Executable.Type.GrantRole);
                var roles = ParseRoleNameList();
                Mustbe(Sqlx.TO);
                var grantees = ParseGranteeList(new PrivNames[] { new PrivNames(Sqlx.USAGE) });
                bool opt = ParseAdminOption();
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.db = tr.AccessRole(cx,true, roles, grantees, opt);
            }
            return (Executable)cx.Add(e);
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
            var n = lxr.val.ToString();
            Mustbe(Sqlx.ID);
            DBObject ob;
            switch (kind)
            {
                case Sqlx.TABLE: 
                case Sqlx.DOMAIN:
                case Sqlx.VIEW:
                case Sqlx.ENTITY:
                case Sqlx.TRIGGER:
                case Sqlx.TYPE: ob = cx.db.GetObject(n) ??
                        throw new DBException("42135",n);
                    break;
                case Sqlx.CONSTRUCTOR: 
                case Sqlx.FUNCTION: 
                case Sqlx.PROCEDURE:
                    {
                        var a = ParseArity();
                        ob = cx.db.GetProcedure(n, a) ??
                        throw new DBException("42108", n+"$"+a);
                        break;
                    }
                case Sqlx.INSTANCE: 
                case Sqlx.STATIC: 
                case Sqlx.OVERRIDING: 
                case Sqlx.METHOD:
                    {
                        int arity = ParseArity();
                        Mustbe(Sqlx.FOR);
                        var tp = lxr.val.ToString();
                        Mustbe(Sqlx.ID);
                        var ut = cx.db.GetObject(tp) as Domain ??
                            throw new DBException("42119", tp);
                        var ui = (ObInfo)cx.db.role.infos[ut.defpos];
                        ob = (DBObject)ut.mem[ui.methods[n][arity]];
                        break;
                    }
                default:
                    throw new DBException("42115", kind).Mix();
            }
            if (ob == null && kind != Sqlx.DATABASE)
                throw new DBException("42135", n).Mix();
            return (cx.Add(ob),n);
        }
        /// <summary>
        /// used in ObjectName. All that is needed here is the number of parameters
        /// because SQL is not strongly typed enough to distinguish actual parameter types
        /// '('Type, {',' Type }')'
        /// </summary>
        /// <returns>the number of parameters</returns>
		int ParseArity()
        {
            int n = 0;
            BTree<long,Domain> fs = BTree<long,Domain>.Empty;
            if (tok == Sqlx.LPAREN)
            {
                Next();
                if (tok == Sqlx.RPAREN)
                {
                    Next();
                    return 0;
                }
                fs+=(lxr.Position,ParseSqlDataType());
                n++;
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    fs+=(lxr.Position,ParseSqlDataType());
                    n++;
                }
                Mustbe(Sqlx.RPAREN);
            }
            return n;
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
                return null;
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
            var s = new List<string>();
            Mustbe(Sqlx.SELECT, Sqlx.DELETE, Sqlx.INSERT, Sqlx.UPDATE,
                Sqlx.REFERENCES, Sqlx.USAGE, Sqlx.TRIGGER, Sqlx.EXECUTE, Sqlx.OWNER);
            if ((r.priv == Sqlx.UPDATE || r.priv == Sqlx.REFERENCES || r.priv == Sqlx.SELECT || r.priv == Sqlx.INSERT) && tok == Sqlx.LPAREN)
            {
                Next();
                s.Add(lxr.val.ToString());
                Mustbe(Sqlx.ID);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    s.Add(lxr.val.ToString());
                    Mustbe(Sqlx.ID);
                }
                Mustbe(Sqlx.RPAREN);
            }
            r.names = s.ToArray();
            return r;
        }
        /// <summary>
		/// GranteeList = PUBLIC | Grantee { ',' Grantee } .
        /// </summary>
        /// <param name="priv">the list of privieges to grant</param>
        /// <returns>the updated database objects</returns>
		DBObject[] ParseGranteeList(PrivNames[] priv)
        {
            if (Match(Sqlx.PUBLIC))
            {
                Next();
                return null;
            }
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
        internal Role GetRole(string n)
        {
            return (cx.db.roles.Contains(n)) ? (Role)cx.db.objects[cx.db.roles[n]] : null;
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
            if (Match(Sqlx.USER))
                Next();
            else if (Match(Sqlx.ROLE))
            {
                kind = Sqlx.ROLE;
                Next();
            }
            var n = lxr.val.ToString();
            Mustbe(Sqlx.ID);
            DBObject ob;
            switch (kind)
            {
                case Sqlx.USER:
                    {
                        ob = GetRole(n) as User;
                        if ((ob == null || ob.defpos == -1) && cx.db is Transaction tr)
                        {
                            cx.Add(new PUser(n, tr.nextPos, cx));
                            ob = GetRole(n);
                        }
                        break;
                    }
                case Sqlx.ROLE: ob = GetRole(n); break;
                default: throw new DBException("28101").Mix();
            }
            if (ob == null && (priv == null || priv.Length != 1 || priv[0].priv != Sqlx.OWNER))
                throw new DBException("28102", kind, n).Mix();
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
		Executable ParseRevoke()
        {
            var e = new Executable(lxr.Position, Executable.Type.Revoke);
            Next();
            Sqlx opt = ParseRevokeOption();
            if (tok == Sqlx.ID)
            {
                var er = new Executable(lxr.Position, Executable.Type.RevokeRole);
                var priv = ParseRoleNameList();
                Mustbe(Sqlx.FROM);
                var grantees = ParseGranteeList(new PrivNames[0]);
                if (opt == Sqlx.GRANT)
                    throw new DBException("42116").Mix();
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.db =tr.AccessRole(cx,false, priv, grantees, opt == Sqlx.ADMIN);
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
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.db = tr.AccessObject(cx,false, priv, ob.defpos, grantees, (opt == Sqlx.GRANT));
            }
            return (Executable)cx.Add(e);
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
		Executable ParseCallStatement(long xp)
        {
            Next();
            Executable e = ParseProcedureCall(xp);
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
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
        /// |   CREATE INDEX Table_id id [UNIQUE] Cols.
        /// </summary>
        /// <returns>the executable</returns>
		Executable ParseCreateClause()
        {
            Next();
            MethodModes();
            Match(Sqlx.TEMPORARY, Sqlx.ROLE, Sqlx.VIEW, Sqlx.TYPE, Sqlx.DOMAIN);
            if (Match(Sqlx.ORDERING))
                return (Executable)cx.Add(ParseCreateOrdering());
            if (Match(Sqlx.XMLNAMESPACES))
                return (Executable)cx.Add(ParseCreateXmlNamespaces());
            if (tok == Sqlx.PROCEDURE || tok == Sqlx.FUNCTION)
            {
                bool func = tok == Sqlx.FUNCTION;
                Next();
                return (Executable)cx.Add(ParseProcedureClause(func, Sqlx.CREATE));
            }
            else if (Match(Sqlx.OVERRIDING, Sqlx.INSTANCE, Sqlx.STATIC, Sqlx.CONSTRUCTOR, Sqlx.METHOD))
                return (Executable)cx.Add(ParseMethodDefinition());
            else if (tok == Sqlx.TABLE || tok == Sqlx.TEMPORARY)
            {
                if (tok == Sqlx.TEMPORARY)
                {
                    Role au = GetRole("Temp");
                    if (au == null)
                        throw new DBException("3D001", "Temp").Mix();
                    cx = new Context(cx, au, cx.db.user);
                    Next();
                }
                Mustbe(Sqlx.TABLE);
                return (Executable)cx.Add(ParseCreateTable());
            }
            else if (tok == Sqlx.TRIGGER)
            {
                Next();
                return (Executable)cx.Add(ParseTriggerDefClause());
            }
            else if (tok == Sqlx.DOMAIN)
            {
                Next();
                return (Executable)cx.Add(ParseDomainDefinition());
            }
            else if (tok == Sqlx.TYPE)
            {
                Next();
                return (Executable)cx.Add(ParseTypeClause());
            }
            else if (tok == Sqlx.ROLE)
            {
                var cr = new Executable(lxr.Position, Executable.Type.CreateRole);
                Next();
                var id = lxr.val.ToString();
                Mustbe(Sqlx.ID);
                TypedValue o = new TChar("");
                if (Match(Sqlx.CHARLITERAL))
                {
                    o = lxr.val;
                    Next();
                }
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.Add(new PRole(id, o.ToString(), tr.nextPos, cx));
                return (Executable)cx.Add(cr);
            }
            else if (tok == Sqlx.VIEW)
                return (Executable)cx.Add(ParseViewDefinition(-1L));
            throw new DBException("42118", tok).Mix();
        }
        /// <summary>
        /// GET option here is Pyrrho shortcut, needs third syntax for ViewDefinition
        /// ViewDefinition = id [ViewSpecification] AS (QueryExpression|GET) {TableMetadata} .
        /// ViewSpecification = Cols 
        ///       | OF id 
        ///       | OF '(' id Type {',' id Type} ')' .
        /// </summary>
        /// <returns>the executable</returns>
        Executable ParseViewDefinition(long qp)
        {
            var ct = new Executable(lxr.Position, Executable.Type.CreateTable);
            Next();
            var id = lxr.val.ToString();
            Mustbe(Sqlx.ID);
            Domain t = Domain.Null;
            long structdef = -1;
            string tn = null; // explicit view type
            Table ut = null;
            bool schema = false; // will record whether a schema for GET follows
            if (Match(Sqlx.LPAREN))
            {
                Next();
                var cd = Domain.Null;
                for (var i=0;;i++ )
                {
                    var n = lxr.val.ToString();
                    var p = lxr.offset + lxr.start;
                    Mustbe(Sqlx.ID);
                    cd += (p, Domain.Content);
                    if (Mustbe(Sqlx.COMMA, Sqlx.RPAREN) == Sqlx.RPAREN)
                        break;
                }
                t = cd;
            }
            else if (Match(Sqlx.OF))
            {
                Next();
                if (Match(Sqlx.LPAREN)) // inline type def
                    t = cx.obs[ParseRowTypeSpec(Sqlx.VIEW)].domain;
                else
                {
                    tn = lxr.val.ToString();
                    Mustbe(Sqlx.ID);
                    t = ((ObInfo)cx.db.objects[cx.db.role.dbobjects[tn]]).domain??
                        throw new DBException("42119", tn, "").Mix();
                }
                schema = true;
            }
            Mustbe(Sqlx.AS);
            Context old = cx;
            cx = new Context(cx);
            var rest = Match(Sqlx.GET);
            QueryExpression qe = null;
            if (!rest)
                qe = ParseQueryExpression(qp,Domain.TableType.defpos);
            else
            {
                if (!schema)
                    throw new DBException("42168").Mix();
                Next();
                if (tok == Sqlx.USING)
                {
                    Next();
                    var idu = lxr.val.ToString();
                    Mustbe(Sqlx.ID);
                    ut = cx.db.GetObject(idu) as Table ??
                        throw new DBException("42107", idu);
                }
            }
            cx = old;
            PView pv = null;
            var tr = cx.tr;
            if (cx.db.parse == ExecuteStatus.Obey)
            {
                var np = cx.db.nextPos;
                if (rest)
                {
                    if (ut == null)
                        pv = new PRestView(id, structdef, np, cx);
                    else
                        pv = new PRestView2(id, structdef, ut.defpos, np, cx);
                }
                else
                    pv = new PView(id, qe, np, cx);
                cx.Add(pv);
            }
            var ob = (DBObject)cx.db.objects[cx.db.loadpos];
            var md = ParseMetadata(ob.defpos, -1, Sqlx.ADD);
            if (md != null && cx.db.parse == ExecuteStatus.Obey)
                new PMetadata(id, -1, pv.ppos, md.description,
                    md.iri, md.seq + 1, md.flags, tr.nextPos, cx);
            return (Executable)cx.Add(ct);
        }
        /// <summary>
        /// Parse the CreateXmlNamespaces syntax
        /// </summary>
        /// <returns>the executable</returns>
        private Executable ParseCreateXmlNamespaces()
        {
            Next();
            var np = cx.db.nextPos;
            var ns = (XmlNameSpaces)ParseXmlNamespaces();
            cx.nsps += (ns.nsps,false);
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
            {
                for (var s = ns.nsps.First(); s != null; s = s.Next())
                    cx.Add(new Namespace(s.key(), s.value(), np, cx));
            }
            return (Executable)cx.Add(ns);
        }
        /// <summary>
        /// Parse the Create ordering syntax:
        /// FOR Domain_id (EQUALS|ORDER FULL) BY (STATE|(MAP|RELATIVE) WITH Func_id)  
        /// </summary>
        /// <returns>the executable</returns>
        private Executable ParseCreateOrdering()
        {
            var co = new Executable(lxr.Position, Executable.Type.CreateOrdering);
            Next();
            Mustbe(Sqlx.FOR);
            var n = new Ident(lxr,Sqlx.ORDER);
            Mustbe(Sqlx.ID);
            Domain ut = cx.db.GetObject(n.ident) as Domain??
                throw new DBException("42133", n).Mix();
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
                fl = fl | OrderCategory.State;
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.Add(new Ordering(ut.defpos, -1L, fl, tr.nextPos, cx));
            }
            else
            {
                fl = fl | ((smr == Sqlx.RELATIVE) ? OrderCategory.Relative : OrderCategory.Map);
                Mustbe(Sqlx.WITH);
                var (fob,nf) = ParseObjectName();
                Procedure func = fob as Procedure;
                if (smr == Sqlx.RELATIVE && func.arity != 2)
                    throw new DBException("42154", nf).Mix();
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.Add(new Ordering(ut.defpos, func.defpos, fl, tr.nextPos, cx));
            }
            return (Executable)cx.Add(co);
        }
        /// <summary>
        /// Cols =		'('id { ',' id } ')'.
        /// </summary>
        /// <returns>a list of Ident</returns>
        BList<Ident> ParseIDList(Sqlx k)
        {
            bool b = (tok == Sqlx.LPAREN);
            if (b)
                Next();
            var r = BList<Ident>.Empty;
            r += ParseIdent(k);
            while (tok == Sqlx.COMMA)
            {
                Next();
                r+=ParseIdent(k);
            }
            if (b)
                Mustbe(Sqlx.RPAREN);
            return r;
        }
        /// <summary>
		/// Cols =		'('ColRef { ',' ColRef } ')'.
        /// </summary>
        /// <returns>a list of coldefpos</returns>
		CList<long> ParseColsList(Table tb)
        {
            var r = CList<long>.Empty;
            bool b = (tok == Sqlx.LPAREN);
            if (b)
                Next();
            r+=ParseColRef(tb).defpos;
            while (tok == Sqlx.COMMA)
            {
                Next();
                r+=ParseColRef(tb).defpos;
            }
            if (b)
                Mustbe(Sqlx.RPAREN);
            return r;
        }
        /// <summary>
        /// ColRef = id { '.' id } .
        /// </summary>
        /// <returns>+- seldefpos</returns>
        TableColumn ParseColRef(Table tb)
        {
            if (tok == Sqlx.PERIOD)
            {
                Next();
                var pn = lxr.val;
                Mustbe(Sqlx.ID);
                var pd = cx.db.objects[tb.applicationPS] as PeriodDef;
                var pi = cx.db.role.infos[pd.defpos] as ObInfo;
                if (pd == null || pi.name != pn.ToString())
                    throw new DBException("42162", pn).Mix();
                return (PeriodDef)cx.Add(pd);
            }
            // We will raise an exception if the first one does not exist
            var id = new Ident(lxr,Sqlx.COLUMN);
            var rt = cx.db.role.infos[tb.defpos] as ObInfo;
            var si = rt.ColFor(cx,id.ident)??
                throw new DBException("42112", id.ident).Mix();
            var tc = (TableColumn)cx.db.objects[si.defpos];
            Mustbe(Sqlx.ID);
            // We will construct paths as required for any later components
            while (tok == Sqlx.DOT)
            {
                Next();
                var pa = new Ident(lxr,Sqlx.COLUMN);
                Mustbe(Sqlx.ID);
                tc = new ColumnPath(pa.iix,pa.ident,tc,cx.db); // returns a (child)TableColumn for non-documents
                long dm = -1;
                if (tok == Sqlx.AS)
                {
                    Next();
                    var at = ParseSqlDataType();
                    if (cx.db.types.Contains(at))
                        at = cx.db.types[at];
                    else if (cx.db is Transaction tr0)
                        cx.db = tr0 + at;
                    tc += (DBObject._Domain, at);
                }
                if (rt.PosFor(cx,pa.ident)<0 && cx.db is Transaction tr) // create a new path 
                    cx.Add(new PColumnPath(si.defpos, pa.ToString(), dm, tr.nextPos, cx));
            }
            return (TableColumn)cx.Add(tc);
        }
        /// <summary>
        /// id [UNDER id] AS Representation [ Method {',' Method} ]
		/// Representation = | StandardType 
        ///             | (' Member {',' Member }')' .
        /// </summary>
        /// <returns>the executable</returns>
        Executable ParseTypeClause()
        {
            var ct = new Executable(lxr.Position, Executable.Type.CreateType);
            var typename = new Ident(lxr,Sqlx.TYPE);
            Ident undername = null;
            if (cx.tr == null)
                throw new DBException("2F003");
            Mustbe(Sqlx.ID);
            if (Match(Sqlx.UNDER))
            {
                Next();
                undername = new Ident(lxr,Sqlx.TYPE);
                Mustbe(Sqlx.ID);
            }
            long underdef = -1L;
            if (undername != null)
            {
                var udm = cx.db.GetObInfo(undername.ident)?.domain ??
                    throw cx.db.Exception("42119", undername).Pyrrho();
                underdef = udm.defpos;
            }
            Mustbe(Sqlx.AS);
            Domain dt;
            if (tok == Sqlx.LPAREN)
                dt = cx.Dom(ParseRowTypeSpec(Sqlx.TYPE, typename, underdef));
            else
                dt = ParseStandardDataType() ??
                    throw new DBException("42161", "StandardType", lxr.val.ToString()).Mix();
            if (Match(Sqlx.RDFLITERAL))
            {
                RdfLiteral rit = (RdfLiteral)lxr.val;
                dt += (Domain.Iri, rit.dataType.iri);
                Next();
            }
            var pt = new PType(typename, -1, dt.defpos, cx.tr.nextPos, cx);
            cx.Add(pt);
            while (Match(Sqlx.CHECK, Sqlx.CONSTRAINT))
                ParseCheckConstraint(pt);
            MethodModes();
            if (Match(Sqlx.OVERRIDING, Sqlx.INSTANCE, Sqlx.STATIC, Sqlx.CONSTRUCTOR, Sqlx.METHOD))
            {
                cx.Frame();
                ParseMethodHeader(pt.ppos);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    ParseMethodHeader(pt.ppos);
                }
            }
            return (Executable)cx.Add(ct);
        }
        /// <summary>
		/// Method =  	MethodType METHOD id '(' Parameters ')' [RETURNS Type] [FOR id].
		/// MethodType = 	[ OVERRIDING | INSTANCE | STATIC | CONSTRUCTOR ] .
        /// </summary>
        /// <returns>the methodname parse class</returns>
        MethodName ParseMethod(long xp)
        {
            MethodName mn = new MethodName
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
            mn.mname = new Ident(lxr,Sqlx.PROCEDURE);
            mn.name = mn.mname.ident; // without the $ which we are just about to add
            Mustbe(Sqlx.ID);
            int st = lxr.start;
            mn.ins = ParseParameters(mn.mname);
            mn.retType = ParseReturnsClause(mn.mname);
            mn.signature = new string(lxr.input, st, lxr.start - st);
            var type = cx.Dom(xp);
            if (tok == Sqlx.FOR)
            {
                Next();
                var tname = new Ident(lxr,Sqlx.TYPE);
                Mustbe(Sqlx.ID);
                type = (Domain)cx.db.objects[cx.db.role.dbobjects[tname.ident]]
                    ?? throw new DBException("42000", "Method").ISO();
            } else if (type==null) // a constructor
                type = (Domain)cx.db.objects[cx.db.role.dbobjects[mn.name]]
                    ?? throw new DBException("42000", "UDType").ISO();
            mn.type = type;
            return mn;
        }
        /// <summary>
        /// Define a new method header (called when parsing CREATE TYPE)(calls ParseMethod)
        /// </summary>
        /// <param name="type">the type name</param>
        /// <returns>the methodname parse class</returns>
		MethodName ParseMethodHeader(long xp)
        {
            MethodName mn = ParseMethod(xp);
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                cx.Add(new PMethod(mn.name, mn.ins,
                    cx.Dom(xp), mn.methodType, mn.type.defpos, 
                    new Ident(mn.signature, mn.mname.iix, Sqlx.PROCEDURE), tr.nextPos, cx));
            return mn;
        }
        /// <summary>
        /// Create a method body (called when parsing CREATE METHOD or ALTER METHOD)
        /// </summary>
        /// <returns>the executable</returns>
		Executable ParseMethodDefinition()
        {
            var cr = new Executable(lxr.Position, Executable.Type.CreateRoutine);
            MethodName mn = ParseMethod(Domain.Null.defpos);
            int st = lxr.start;
            Domain ut = mn.type;
            var ui = (ObInfo)cx.db.role.infos[ut.defpos];
            var meth = cx.db.objects[ui.methods[mn.name]?[mn.arity]??-1L] as Method??
                throw new DBException("42132", mn.mname.ToString(), ui.name).Mix();
            for (var b = ui.domain.representation.First(); b != null; b = b.Next())
            {
                var p = b.key();
                var oi = cx.NameFor(p);
                var ob = cx.obs[p];
                cx.defs += (new Ident(oi, p, Sqlx.COLUMN), ob);
            }
            meth +=(Procedure.Params, mn.ins);
            meth += (Procedure.Body, cx.Add(ParseProcedureStatement(mn.retType.defpos)).defpos);
            string ss = new string(lxr.input,st, lxr.start - st);
            // we really should check the signature here
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
               cx.Add(new Modify(mn.mname.ident, meth.defpos, mn.signature + " " + ss,
                    meth, tr.nextPos, cx));
            return (Executable)cx.Add(cr);
        }
        /// <summary>
        /// DomainDefinition = id [AS] StandardType [DEFAULT TypedValue] { CheckConstraint } Collate.
        /// </summary>
        /// <returns>the executable</returns>
        Executable ParseDomainDefinition()
        {
            var cd = new Executable(lxr.Position, Executable.Type.CreateDomain);
            var colname = new Ident(lxr,Sqlx.COLUMN);
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.AS)
                Next();
            var type = ParseSqlDataType();
            if (tok == Sqlx.DEFAULT)
            {
                Next();
                int st = lxr.start;
                var dv = lxr.val;
                Next();
                var ds = new string(lxr.input, st, lxr.start - st);
                type = type + (Domain.Default, dv) + (Domain.DefaultString, ds);
            }
            PDomain pd = null;
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var np = tr.nextPos;
            if (type.iri != null)
                pd = new PDomain1(colname.ident, type, np, cx);
            else
                pd = new PDomain(colname.ident, type, np, cx);
            cx.Add(pd);
            var a = new List<Physical>();
            while (Match(Sqlx.NOT, Sqlx.CONSTRAINT, Sqlx.CHECK))
                a.Add(ParseCheckConstraint(pd));
            if (tok == Sqlx.COLLATE)
                pd.culture = new CultureInfo(ParseCollate());
            return (Executable)cx.Add(cd);
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
        PCheck ParseCheckConstraint(PDomain pd)
        {
            var o = new Ident(lxr,Sqlx.CHECK);
            Ident n = null;
            var st = lxr.start;
            cx.Frame();
            if (tok == Sqlx.CONSTRAINT)
            {
                Next();
                n = o;
                Mustbe(Sqlx.ID);
            }
            else
                n = new Ident(lxr,Sqlx.CHECK);
            Mustbe(Sqlx.CHECK);
            Mustbe(Sqlx.LPAREN);
            st = lxr.start;
            var se = ParseSqlValue(pd.defpos,Domain.Bool.defpos).Reify(cx);
            Mustbe(Sqlx.RPAREN);
            PCheck pc = null;
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
            {
                pc = new PCheck(pd.defpos, n.ident, se,
                    new string(lxr.input, st, lxr.start - st), tr.nextPos, cx);
                pc.Frame(cx);
                cx.Add(pc);
            }
            return pc;
        }
        /// <summary>
        /// id TableContents [UriType] [Clasasification] [Enforcement] {Metadata} 
        /// </summary>
        /// <returns>the executable</returns>
        Executable ParseCreateTable()
        {
            var ct = new Executable(lxr.Position, Executable.Type.CreateTable);
            var name = new Ident(lxr,Sqlx.TABLE);
            PTable pt = null;
            Mustbe(Sqlx.ID);
            if (cx.db.schemaRole.dbobjects.Contains(name.ident) || cx.db.role.dbobjects.Contains(name.ident))
                throw new DBException("42104", name);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var np = tr.nextPos;
            pt = new PTable(name.ident, np, cx);
            cx.Add(pt);
            var ns = new BTree<long,ObInfo>(pt.defpos,
                new ObInfo(pt.defpos, pt.name, Sqlx.TABLE, Domain.TableType));
            ns = ParseTableContentsSource(pt.defpos, ns);
            var ta = (Table)cx.obs[pt.defpos];
            if (tok == Sqlx.RDFLITERAL)
            {
                RdfLiteral rit = (RdfLiteral)lxr.val;
                ta += (DBObject._Domain, ta.domain+(Domain.Iri,rit.dataType.iri));
                Next();
            }
            cx.Add(ta);
            if (Match(Sqlx.SECURITY))
            {
                Next();
                if (Match(Sqlx.LEVEL))
                    ns = ParseClassification(ta.defpos,ns);
                if (tok == Sqlx.SCOPE)
                    ns = ParseEnforcement(ta.defpos,ns);
            }
            return (Executable)cx.Add(ct);
        }
        BTree<long,ObInfo> ParseClassification(long pt,BTree<long,ObInfo> ns)
        {
            var lv = MustBeLevel();
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                cx.Add(new Classify(pt, lv, cx.db.nextPos, cx));
            return ns;
        }
        /// <summary>
        /// Enforcement = SCOPE [READ] [INSERT] [UPDATE] [DELETE]
        /// </summary>
        /// <returns></returns>
        BTree<long,ObInfo> ParseEnforcement(long pt,BTree<long,ObInfo> ns)
        {
            if (cx.db.user.defpos != cx.db.owner)
                throw new DBException("42105");
            Mustbe(Sqlx.SCOPE);
            var lp = lxr.Position;
            Grant.Privilege r = Grant.Privilege.NoPrivilege;
            while (Match(Sqlx.READ,Sqlx.INSERT,Sqlx.UPDATE,Sqlx.DELETE))
            {
                switch(tok)
                {
                    case Sqlx.READ: r |= Grant.Privilege.Select; break;
                    case Sqlx.INSERT: r |= Grant.Privilege.Insert; break;
                    case Sqlx.UPDATE: r |= Grant.Privilege.Update; break;
                    case Sqlx.DELETE: r |= Grant.Privilege.Delete; break;
                }
                Next();
            }
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                cx.Add(new Enforcement(pt, r, tr.nextPos, cx));
            return ns;
        }
        /// <summary>
        /// TebleContents = '(' TableClause {',' TableClause } ')' { VersioningClause }
        /// | OF Type_id ['(' TypedTableElement { ',' TypedTableElement } ')'] .
        /// VersioningClause = WITH (SYSTEM|APPLICATION) VERSIONING .
        /// </summary>
        /// <param name="ta">The newly defined Table</param>
        /// <returns>The iri or null</returns>
        BTree<long,ObInfo> ParseTableContentsSource(long t,BTree<long,ObInfo> ns=null)
        {
            ns = ns ?? BTree<long, ObInfo>.Empty;
            switch (tok)
            {
                case Sqlx.LPAREN:
                    Next();
                    ns = ParseTableItem(t,ns);
                    while (tok == Sqlx.COMMA)
                    {
                        Next();
                        ns = ParseTableItem(t,ns);
                    }
                    Mustbe(Sqlx.RPAREN);
                    while (Match(Sqlx.WITH))
                        ParseVersioningClause(t, false);
                    break;
                case Sqlx.OF:
                    {
                        Next();
                        var id = ParseIdent(Sqlx.TYPE);
                        var udt = cx.db.GetObject(id.ident) as Domain??
                            throw new DBException("42133", id.ToString()).Mix();
                        var ui = cx.db.role.infos[udt.defpos] as ObInfo;
                        var tr = cx.db as Transaction?? throw new DBException("2F003");
                        for (var cd = ui.rowType?.First(); cd != null; cd = cd.Next())
                        {
                            var p = cd.value().Item1;
                            var tc = cx.db.objects[p] as TableColumn;
                            var ci = cx.NameFor(p);
                            cx.Add(new PColumn2(t, ci, cd.key(), tc.domain.defpos, 
                                tc.generated.gfs??tc.domain.defaultValue?.ToString()??"", 
                                tc.domain.defaultValue, tc.notNull, 
                                tc.generated, tr.nextPos, cx));
                        }
                        if (Match(Sqlx.LPAREN))
                        {
                            for (; ; )
                            {
                                Next();
                                if (Match(Sqlx.UNIQUE, Sqlx.PRIMARY, Sqlx.FOREIGN))
                                    ns = ParseTableConstraintDefin(t,ns);
                                else
                                {
                                    id = ParseIdent(Sqlx.NONE);
                                    var se = ui.ColFor(cx,id.ident);
                                    if (se == null)
                                        throw new DBException("42112", id.ToString()).Mix();
                                    ns = ParseColumnOptions(t, se.defpos,ns);
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
            return ns;
        }
        /// <summary>
        /// Parse the table versioning clause:
        /// (SYSTEM|APPLICATION) VERSIONING
        /// </summary>
        /// <param name="ta">the table</param>
        /// <param name="drop">whether we are dropping an existing versioning</param>
        /// <returns>the updated Table object</returns>
        private Table ParseVersioningClause(long t, bool drop)
        {
            Next();
            var sa = tok;
            var lp = lxr.Position;
            Mustbe(Sqlx.SYSTEM, Sqlx.APPLICATION);
            var vi = (sa == Sqlx.SYSTEM) ? PIndex.ConstraintType.SystemTimeIndex : PIndex.ConstraintType.ApplicationTimeIndex;
            PIndex pi = null;
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var ta = (Table)cx.obs[t];
            if (drop)
            {
                var fl = (vi == 0) ? PIndex.ConstraintType.SystemTimeIndex : PIndex.ConstraintType.ApplicationTimeIndex;
                for (var e = ta.indexes.First(); e != null; e = e.Next())
                {
                    var px = cx.db.objects[e.value()] as PIndex;
                    if (px.tabledefpos == t && (px.flags & fl) == fl)
                        cx.Add(new Drop(px.defpos, tr.nextPos, cx));
                }
                if (sa == Sqlx.SYSTEM)
                {
                    var pd = cx.db.objects[ta.systemPS] as PeriodDef;
                    cx.Add(new Drop(pd.defpos, tr.nextPos, cx));
                }
                Mustbe(Sqlx.VERSIONING);
                return (Table)cx.Add(ta);
            }
            if (sa == Sqlx.SYSTEM)
            {
                // ??
            }
            Index ix = ta.FindPrimaryIndex(cx.db);
            var ti = cx.db.role.infos[ta.defpos] as ObInfo;
            if (pi == null && sa == Sqlx.APPLICATION)
                throw new DBException("42164", ti.name).Mix();
            if (pi != null)
                cx.Add(new PIndex("", ta.defpos, pi.columns,
                    PIndex.ConstraintType.PrimaryKey | vi,-1L, tr.nextPos, cx));
            Mustbe(Sqlx.VERSIONING);
            return (Table)cx.Add(ta);
        }
        /// <summary>
        /// TypedTableElement = id WITH OPTIONS '(' ColumnOption {',' ColumnOption} ')'.
        /// ColumnOption = (SCOPE id)|(DEFAULT TypedValue)|ColumnConstraint .
        /// </summary>
        /// <param name="tb">The table define created</param>
        /// <param name="tc">The column beging optioned</param>
        BTree<long,ObInfo> ParseColumnOptions(long t, long c,BTree<long,ObInfo>ns)
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
                            var se = (TableColumn)cx.obs[c];
                            var dfv = se.domain.Parse(lxr.Position,new string(lxr.input, st, lxr.start - st));
                            cx.db += (se + (DBObject._Domain, se.domain + (Domain.Default, dfv)));
                            break;
                        }
                    default: ns = ParseColumnConstraint(t, c, ns);
                        break;
                }
                if (Match(Sqlx.COMMA))
                    Next();
                else
                    break;
            }
            Mustbe(Sqlx.RPAREN);
            return ns;
        }
        /// <summary>
        /// TableClause =	ColumnDefinition | TableConstraintDef | TablePeriodDefinition .
        /// </summary>
        /// <param name="tb">the table</param>
        /// <returns>the updated Table</returns>
        BTree<long,ObInfo> ParseTableItem(long t,BTree<long,ObInfo>ns=null)
        {
            ns = ns ?? BTree<long, ObInfo>.Empty;
            if (Match(Sqlx.PERIOD))
                ns = AddTablePeriodDefinition(t,ns);
            else if (tok == Sqlx.ID)
                ns = ParseColumnDefin(t,ns);
            else
                ns = ParseTableConstraintDefin(t,ns);
            return ns;
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
                r.periodname = new Ident(lxr,Sqlx.PERIOD);
                Mustbe(Sqlx.ID);
                r.pkind = Sqlx.APPLICATION;
            }
            Mustbe(Sqlx.LPAREN);
            r.col1 = ParseIdent(Sqlx.COLUMN);
            Mustbe(Sqlx.COMMA);
            r.col2 = ParseIdent(Sqlx.COLUMN);
            Mustbe(Sqlx.RPAREN);
            return r;
        }
        /// <summary>
        /// Add columns for table period definition
        /// </summary>
        /// <param name="tb"></param>
        /// <returns>the updated table</returns>
        BTree<long,ObInfo> AddTablePeriodDefinition(long t,BTree<long,ObInfo> ns)
        {
            var ptd = ParseTablePeriodDefinition();
            var rt = ns[t];
            var c1 = rt.ColFor(cx,ptd.col1.ident);
            var c2 = rt.ColFor(cx,ptd.col2.ident);
            var c1t = c1?.domain;
            var c2t = c2?.domain;
            if (c1 == null)
                throw new DBException("42112", ptd.col1).Mix();
            if (c1t.prim != Sqlx.DATE && c2t.prim != Sqlx.TIMESTAMP)
                throw new DBException("22005R", "DATE or TIMESTAMP", c1t.ToString()).ISO()
                    .AddType(Domain.UnionDate).AddValue(c1t);
            if (c1 == null)
                throw new DBException("42112", ptd.col2).Mix();
            if (c1t.CompareTo(c2t)!=0)
                throw new DBException("22005S", c1t.ToString(), c2t.ToString()).ISO()
                    .AddType(c1t).AddValue(c2t);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            cx.Add(new PPeriodDef(t, ptd.periodname.ident, c1.defpos, c2.defpos,
                tr.nextPos, cx));
            return ns;
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
        BTree<long,ObInfo> ParseColumnDefin(long t,BTree<long,ObInfo> ns)
        {
            Domain type = null;
            Domain dom = null;
            var colname = new Ident(lxr,Sqlx.COLUMN);
            long lp = lxr.Position;
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.ID)
            {
                var op = cx.db.role.dbobjects[new Ident(lxr,Sqlx.DOMAIN).ident];
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
                Sqlx.DOCUMENT, Sqlx.DOCARRAY,
#if MONGO
                Sqlx.OBJECT, // v5.1
#endif
                Sqlx.ROW, Sqlx.TABLE, Sqlx.REF))
                type = ParseSqlDataType();
            dom = type;
            if (Match(Sqlx.ARRAY))
            {
                dom = new Domain(lp,Sqlx.ARRAY, type);
                Next();
            }
            else if (Match(Sqlx.MULTISET))
            {
                dom = new Domain(lp,Sqlx.MULTISET, type);
                Next();
            }
            /*           if (tok == Sqlx.RDFLITERAL)
                       {
                           if (lxr.val is RdfLiteral owl)
                           {
                               Domain.GetOrCreateDomain(lxr,lxr.db, ref type);
                               type = type.Copy(owl.dataType.iri);
                               if (owl.val != null && owl.ToString() != "")
                                   type.abbrev = owl.ToString();
                               //                if (owl.type.iri ==IriRef.BSON)
                               //                  Bson.Parse(trans,owl);
                           }
                           Next();
                       } */
            var ua = BList<UpdateAssignment>.Empty;
            var gr = GenerationRule.None;
            var dfs = "";
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
                dv = dom?.defaultValue ?? TNull.Value;
                dfs = dom?.defaultString ?? "";
                // Set up the information for parsing the generation rule
                // The table domain and cx.defs should contain the columns so far defined
                var oc = cx;
                cx = cx.ForConstraintParse(ns);
                gr = ParseGenerationRule(dom.defpos)+(DBObject.Framing,cx.obs);
                cx = oc;
            }
            if (dom == null)
                throw new DBException("42120", colname.ident);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var tb = (Table)cx.db.objects[t];
            var pc = new PColumn3(t, colname.ident, tb.domain.Length, dom.defpos,
                gr?.gfs ?? dfs, dv, "", ua, false, gr, tr.nextPos, cx);
            cx.Add(pc);
            tb = (Table)cx.obs[t];
            ns += (pc.ppos, new ObInfo(pc.ppos, colname.ident, Sqlx.COLUMN, dom));
            ns += (tb.defpos, new ObInfo(t, tb.domain, cx._RowType(t) + (pc.ppos,dom)));
            while (Match(Sqlx.NOT, Sqlx.REFERENCES, Sqlx.CHECK, Sqlx.UNIQUE, Sqlx.PRIMARY, Sqlx.CONSTRAINT,
                Sqlx.SECURITY))
                ns = ParseColumnConstraintDefin(t, pc, ns);
            if (type != null && tok == Sqlx.COLLATE)
                dom = new Domain(pc.ppos,type.prim,
                    type.mem+(Domain.Culture,new CultureInfo(ParseCollate())));
            return ns;
        }
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
                case Sqlx.TABLE: return Match(Sqlx.ENTITY, Sqlx.PIE, Sqlx.HISTOGRAM, Sqlx.LEGEND, Sqlx.LINE, Sqlx.POINTS, Sqlx.DROP,
                    Sqlx.JSON, Sqlx.CSV, Sqlx.CHARLITERAL, Sqlx.RDFLITERAL, Sqlx.REFERRED);
                case Sqlx.COLUMN: return Match(Sqlx.ATTRIBUTE, Sqlx.X, Sqlx.Y, Sqlx.CAPTION, Sqlx.DROP, Sqlx.CHARLITERAL, Sqlx.RDFLITERAL,
                    Sqlx.REFERS);
                case Sqlx.PROCEDURE: return Match(Sqlx.ENTITY, Sqlx.PIE, Sqlx.HISTOGRAM, Sqlx.LEGEND,
                    Sqlx.LINE, Sqlx.POINTS, Sqlx.DROP, Sqlx.JSON, Sqlx.CSV, Sqlx.INVERTS, Sqlx.MONOTONIC);
                default: return Match(Sqlx.CHARLITERAL, Sqlx.RDFLITERAL);
            }
        }
        /// <summary>
        /// Parse ([ADD]|DROP) Metadata
        /// </summary>
        /// <param name="tr">the database</param>
        /// <param name="ob">the object the metadata is for</param>
        /// <param name="seq">the position of the metadata</param>
        /// <param name="kind">the metadata</param>
        Metadata ParseMetadata(long dp, int seq, Sqlx kind)
        {
            var drop = false;
            if (Match(Sqlx.ADD))
                Next();
            else if (Match(Sqlx.DROP))
            {
                drop = true;
                Next();
            }
            return ParseMetadata(dp, seq, kind, drop);
        }
        /// <summary>
        /// Parse Metadata
        /// </summary>
        /// <param name="tr">the database</param>
        /// <param name="ob">the object to be affected</param>
        /// <param name="seq">the position in the metadata</param>
        /// <param name="kind">the metadata</param>
        /// <param name="drop">whether drop</param>
        /// <returns>the metadata</returns>
        Metadata ParseMetadata(long dp, int seq, Sqlx kind, bool drop)
        {
            Metadata m = null;
            var checkedRole = 0;
            var pr = ((ObInfo)cx.db.role.infos[dp])?.priv??Grant.Privilege.NoPrivilege;
            while (StartMetadata(kind))
            {
                if (checkedRole++ == 0 && (cx.db.role == null || !pr.HasFlag(Grant.Privilege.AdminRole)))
                    throw new DBException("42105");
                if (m == null)
                    m = new Metadata();
                var o = lxr.val;
                switch (tok)
                {
                    case Sqlx.CHARLITERAL: m.description = drop ? "" : o.ToString(); break;
                    case Sqlx.RDFLITERAL:
                        if (!pr.HasFlag(Grant.Privilege.Owner))
                            throw new DBException("42105");
                        m.iri = drop ? "" : o.ToString();
                        break;
                    case Sqlx.ID:
                        {
                            var ic = new Ident(lxr,Sqlx.VALUE);
                            var x = cx.db.GetObject(ic.ident)??
                                throw new DBException("42108", lxr.val.ToString()).Pyrrho();
                            m.refpos = x.defpos;
                            break;
                        }
                    default:
                        {
                            var ic = new Ident(lxr,Sqlx.VALUE);
                            if (drop)
                                m.Drop(tok);
                            else
                                m.Add(tok);
                            break;
                        }
                }
                Next();
            }
            return m;
        }
        /// <summary>
        /// GenerationRule =  GENERATED ALWAYS AS '('Value')' [ UPDATE '(' Assignments ')' ]
        /// |   GENERATED ALWAYS AS ROW (START|END) .
        /// </summary>
        /// <param name="rt">The expected type</param>
        GenerationRule ParseGenerationRule(long xp)
        {
            var gr = GenerationRule.None;
            if (Match(Sqlx.GENERATED))
            {
                Next();
                Mustbe(Sqlx.ALWAYS);
                Mustbe(Sqlx.AS);
                if (Match(Sqlx.ROW))
                {
                    Next();
                    switch (tok)
                    {
                        case Sqlx.START: gr = new GenerationRule(Generation.RowStart); break;
                        case Sqlx.END: gr = new GenerationRule(Generation.RowEnd); break;
                        default: throw new DBException("42161", "START or END", tok.ToString()).Mix();
                    }
                    Next();
                }
                else
                {
                    int st = lxr.start;
                    var gnv = ParseSqlValue(xp,xp).Reify(cx);
                    var s = new string(lxr.input, st, lxr.start - st);
                    gr = new GenerationRule(Generation.Expression, s, gnv);
                }
            }
            return gr;
        }
        /// <summary>
        /// Parse a columnconstraintdefinition
        /// ColumnConstraint = [ CONSTRAINT id ] ColumnConstraintDef
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="pc">the column (Level2)</param>
        /// <returns>the updated table</returns>
		BTree<long,ObInfo> ParseColumnConstraintDefin(long t, PColumn2 pc, BTree<long,ObInfo>ns)
        {
            Ident name = null;
            if (tok == Sqlx.CONSTRAINT)
            {
                Next();
                name = new Ident(lxr,Sqlx.VALUE);
                Mustbe(Sqlx.ID);
            }
            else
                name = new Ident(lxr,Sqlx.VALUE);
            if (tok == Sqlx.NOT)
            { 
                Next();
                if (pc != null)
                { 
                    pc.notNull = true;
                    if (cx.db is Transaction tr)
                        cx.db += (Transaction.Physicals,tr.physicals + (pc.defpos, pc));
                }
                Mustbe(Sqlx.NULL);
                // Updating the database to add NOT NULL at this stage is ridiculously tricky
                var ro = cx.db.role;
                var tb = (Table)cx.obs[t];
                var tc = (TableColumn)cx.obs[pc.ppos];
                var ti = (ObInfo)ro.infos[tb.defpos];
                var ci = (ObInfo)ro.infos[tc.defpos];
                tc += (Domain.NotNull, true);
                ci += (DBObject._Domain,tc.domain);
                ti += (tc.defpos, tc.domain);
                tb += tc;
                ro = ro + ti + ci;
                cx.db = cx.db + ro + (tb,cx.db.loadpos) + (tc,cx.db.loadpos);
                cx.Add(tb);
                cx.Add(tc);
            }
            else
                ns = ParseColumnConstraint(t, pc.ppos, ns);
            return ns;
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
		BTree<long,ObInfo> ParseColumnConstraint(long t, long c, BTree<long,ObInfo> ns)
        {
            var tc = (TableColumn)cx.obs[c];
            var key = new CList<long>(c);
            var lp = lxr.Position;
            string nm = "";
            switch (tok)
            {
                case Sqlx.SECURITY:
                    Next();
                    ns =ParseClassification(c,ns); break;
                case Sqlx.REFERENCES:
                    {
                        Next();
                        var refname = new Ident(lxr,Sqlx.NONE);
                        var rt = cx.db.GetObject(refname.ident) as Table??
                            throw new DBException("42107", refname).Mix();
                        CList<long> cols = null;
                        Mustbe(Sqlx.ID);
                        if (tok == Sqlx.LPAREN)
                            cols = ParseColsList(rt);
                        string afn = "";
                        if (tok == Sqlx.USING)
                        {
                            Next();
                            int st = lxr.start;
                            if (tok == Sqlx.ID)
                            {
                                Next();
                                var ni = new Ident(lxr,Sqlx.PROCEDURE);
                                var pr = cx.db.GetProcedure(ni.ident,1) ??
                                    throw new DBException("42108", ni).Pyrrho();
                                afn = "\"" + pr.defpos + "\"";
                            }
                            else
                            {
                                var sp = lxr.Position;
                                Mustbe(Sqlx.LPAREN);
                                ParseSqlValueList(t,Domain.Content.defpos);
                                Mustbe(Sqlx.RPAREN);
                                afn = new string(lxr.input,st, lxr.start - st);
                            }
                        }
                        var ct = ParseReferentialAction();
                        cx.db = cx.tr.AddReferentialConstraint(cx, 
                            (Table)cx.obs[t], new Ident("", 0, Sqlx.VALUE), key, rt, 
                            cx._Pick(cx._RowType(rt.defpos), cols), ct, afn);
                        break;
                    }
                case Sqlx.CONSTRAINT:
                    {
                        Next();
                        nm = new Ident(lxr,Sqlx.CHECK).ident;
                        Mustbe(Sqlx.ID);
                        if (tok != Sqlx.CHECK)
                            throw new DBException("42161", "CHECK",tok);
                        goto case Sqlx.CHECK;
                    }
                case Sqlx.CHECK:
                    {
                        Next();
                        ns = ParseColumnCheckConstraint(t, c, nm, ns);
                        break;
                    }
                case Sqlx.UNIQUE:
                    {
                        Next();
                        var tr = cx.db as Transaction?? throw new DBException("2F003");
                        cx.Add(new PIndex(nm, t, key, PIndex.ConstraintType.Unique, -1L,
                            tr.nextPos,cx));
                        break;
                    }
                case Sqlx.PRIMARY:
                    {
                        var tb = (Table)cx.obs[t];
                        Index x = tb.FindPrimaryIndex(cx.db);
                        var ti = ns[t];
                        if (x != null)
                            throw new DBException("42147", ti.name).Mix();
                        Next();
                        Mustbe(Sqlx.KEY);
                        var tr = cx.db as Transaction?? throw new DBException("2F003");
                        cx.Add(new PIndex(ti.name, t, key, PIndex.ConstraintType.PrimaryKey, 
                            -1L, tr.nextPos, cx));
                        break;
                    }
            }
            return ns;
        }
        /// <summary>
        /// TableConstraint = [CONSTRAINT id ] TableConstraintDef .
		/// TableConstraintDef = UNIQUE Cols
		/// |	PRIMARY KEY  Cols
		/// |	FOREIGN KEY Cols REFERENCES Table_id [ Cols ] { ReferentialAction } .
        /// </summary>
        /// <param name="tb">the table</param>
        /// <returns>the updated table</returns>
		BTree<long,ObInfo> ParseTableConstraintDefin(long t,BTree<long,ObInfo>ns)
        {
            Ident name = null;
            if (tok == Sqlx.CONSTRAINT)
            {
                Next();
                name = new Ident(lxr,Sqlx.CHECK);
                Mustbe(Sqlx.ID);
            }
            else
                name = new Ident(lxr,Sqlx.CHECK);
            Sqlx s = Mustbe(Sqlx.UNIQUE, Sqlx.PRIMARY, Sqlx.FOREIGN, Sqlx.CHECK);
            switch (s)
            {
                case Sqlx.UNIQUE: ParseUniqueConstraint(t, name); break;
                case Sqlx.PRIMARY: ParsePrimaryConstraint(t, name); break;
                case Sqlx.FOREIGN: ParseReferentialConstraint(t, name); break;
                case Sqlx.CHECK: ParseTableConstraint(t, name); break;
            }
            return ns;
        }
        /// <summary>
        /// construct a unique constraint
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="name">the constraint name</param>
        /// <returns>the updated table</returns>
        Table ParseUniqueConstraint(long t, Ident name)
        {
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            cx.Add(new PIndex(name.ident, t, ParseColsList((Table)cx.obs[t]), 
                PIndex.ConstraintType.Unique, -1L, tr.nextPos, cx));
            return (Table)cx.Add(tr,t);
        }
        /// <summary>
        /// construct a primary key constraint
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="cl">the ident</param>
        /// <param name="name">the constraint name</param>
        Table ParsePrimaryConstraint(long t, Ident name)
        {
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            var tb = (Table)cx.db.objects[t];
            Index x = tb.FindPrimaryIndex(cx.db);
            var ti = cx.db.role.infos[t] as ObInfo;
            if (x != null)
                throw new DBException("42147", ti.name).Mix();
            Mustbe(Sqlx.KEY);
            cx.Add(new PIndex(name.ident, t, ParseColsList(tb), 
                PIndex.ConstraintType.PrimaryKey, -1L, tr.nextPos, cx));
            return (Table)cx.Add(tr,t);
        }
        /// <summary>
        /// construct a referential constraint
        /// id [ Cols ] { ReferentialAction }
        /// </summary>
        /// <param name="tb">the table</param>
        /// <param name="name">the constraint name</param>
        /// <returns>the updated table</returns>
        Table ParseReferentialConstraint(long t, Ident name)
        {
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            Mustbe(Sqlx.KEY);
            var tb = (Table)cx.obs[t];
            var cols = ParseColsList(tb);
            var lp = lxr.Position;
            Mustbe(Sqlx.REFERENCES);
            var refname = new Ident(lxr,Sqlx.NONE);
            var rt = cx.db.GetObject(refname.ident) as Table??
                throw new DBException("42107", refname).Mix();
            Mustbe(Sqlx.ID);
            var refs = CList<long>.Empty;
            PIndex.ConstraintType ct = PIndex.ConstraintType.ForeignKey;
            if (tok == Sqlx.LPAREN)
                refs = ParseColsList(rt);
            string afn = "";
            if (tok == Sqlx.USING)
            {
                Next();
                int st = lxr.start;
                if (tok == Sqlx.ID)
                {
                    var ic = new Ident(lxr,Sqlx.PROCEDURE);
                    Next();
                    var pr = cx.db.GetProcedure(ic.ident,(int)refs.Count)??
                        throw new DBException("42108", ic.ident+"$"+refs.Count).Pyrrho();
                    afn = "\"" + pr.defpos + "\"";
                }
                else
                {
                    var sp = lxr.Position;
                    Mustbe(Sqlx.LPAREN);
                    ParseSqlValueList(t,Domain.Content.defpos);
                    Mustbe(Sqlx.RPAREN);
                    afn = new string(lxr.input,st, lxr.start - st);
                }
            }
            if (tok == Sqlx.ON)
                ct |= ParseReferentialAction();
            cx.db = tr.AddReferentialConstraint(cx,tb, name, cols, rt, 
                cx._Pick(cx._RowType(rt.defpos), refs), ct, afn);
            return (Table)cx.Add(tr,tb.defpos);
        }
        /// <summary>
        /// CheckConstraint = [ CONSTRAINT id ] CHECK '(' [XMLOption] SqlValue ')' .
        /// </summary>
        /// <param name="tb">The table</param>
        /// <param name="name">the name of the constraint</param
        /// <returns>the new PCheck</returns>
        void ParseTableConstraint(long t, Ident name)
        {
            int st = lxr.start;
            Mustbe(Sqlx.LPAREN);
            cx.Frame();
            var se = ParseSqlValue(t,Domain.Bool.defpos);
            Mustbe(Sqlx.RPAREN);
            var n = name ?? new Ident(lxr,Sqlx.VALUE);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            PCheck r = new PCheck(t, n.ident, se,
                new string(lxr.input,st, lxr.start - st), tr.nextPos, cx);
            cx.Add(r);
            r.Frame(cx);
            if (t < Transaction.TransPos)
                new From(new Ident("",-1,Sqlx.VALUE),cx,(Table)cx.obs[t]).TableCheck(tr,r);
        }
        /// <summary>
        /// CheckConstraint = [ CONSTRAINT id ] CHECK '(' [XMLOption] SqlValue ')' .
        /// </summary>
        /// <param name="tb">The table</param>
        /// <param name="pc">the column constrained</param>
        /// <param name="name">the name of the constraint</param>
        /// <returns>the new PCheck</returns>
        BTree<long,ObInfo> ParseColumnCheckConstraint(long t, long c,string nm,BTree<long,ObInfo> ns)
        {
            int st = lxr.start;
            var lp = lxr.Position;
            cx.Frame();
            Mustbe(Sqlx.LPAREN);
            var tb = (Table)cx.db.objects[t];
            var tc = (TableColumn)cx.db.objects[c];
            cx.obs += (tb.defpos,tb);
            cx.obs += (tc.defpos, tc);
            cx.obs += (tb.framing,true);
            // Set up the information for parsing the column check constraint
            var oi = ns[tb.defpos];
            cx.defs += (new Ident(oi.name, tb.defpos, Sqlx.TABLE), tb);
            for (var b = ns.First(); b != null; b = b.Next())
            {
                var cp = b.key();
                if (cp == tb.defpos)
                    continue;
                var ci = b.value();
                oi += (oi.Length, cp, ci.domain);
                cx.defs += (ci.name, cp,Sqlx.COLUMN,  Ident.Idents.Empty);
            }
            var se = ParseSqlValue(t,Domain.Bool.defpos);
            Mustbe(Sqlx.RPAREN);
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var pc = new PCheck2(tb.defpos, tc.defpos, nm, se,
                new string(lxr.input, st, lxr.start - st), tr.nextPos, cx);
            pc.Frame(cx);
            cx.Add(pc);
            return ns;
        }
        /// <summary>
		/// ReferentialAction = {ON (DELETE|UPDATE) (CASCADE| SET DEFAULT|RESTRICT)} .
        /// </summary>
        /// <returns>constraint type flags</returns>
		PIndex.ConstraintType ParseReferentialAction()
        {
            PIndex.ConstraintType r = PIndex.ConstraintType.ForeignKey;
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
                    switch (what)
                    {
                        case Sqlx.CASCADE: r |= PIndex.ConstraintType.CascadeUpdate; break;
                        case Sqlx.DEFAULT: r |= PIndex.ConstraintType.SetDefaultUpdate; break;
                        case Sqlx.NULL: r |= PIndex.ConstraintType.SetNullUpdate; break;
                    }
                else
                    switch (what)
                    {
                        case Sqlx.CASCADE: r |= PIndex.ConstraintType.CascadeDelete; break;
                        case Sqlx.DEFAULT: r |= PIndex.ConstraintType.SetDefaultDelete; break;
                        case Sqlx.NULL: r |= PIndex.ConstraintType.SetNullDelete; break;
                    }
            }
            return r;
        }
        public void ParseProcedure(PProcedure pp)
        {
            long oldStart = cx.parseStart; // safety
            cx.parseStart = lxr.Position;
            var os = cx.nextStmt;
            // we temporarily place a bodyless version of the procedure in the context
            var pr = new Procedure(pp,cx,new BTree<long, object>(Basis.Name,pp.name));
            if (!Match(Sqlx.EOF, Sqlx.SEMICOLON))
            {
                cx.AddProc(pr);
                cx.AddParams(pr);
                if (pr is Method mt)
                    cx.AddDefs(mt.udType, cx.db);
                pp.body = cx.Add(ParseProcedureStatement(pr.defpos)).defpos;
            }
            cx.parseStart = oldStart;
            cx.nextStmt = os;
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
        public Executable ParseProcedureClause(bool func, Sqlx create)
        {
            var cr = new Executable(lxr.Position, (create == Sqlx.CREATE) ? Executable.Type.CreateRoutine : Executable.Type.AlterRoutine);
            var n = new Ident(lxr,Sqlx.PROCEDURE);
            Mustbe(Sqlx.ID);
            int st = lxr.start;
            var ps = ParseParameters(n);
            var arity = (int)ps.Count;
            var rdt = Domain.Null;
            if (func)
                rdt = ParseReturnsClause(n);
            if (Match(Sqlx.EOF) && create == Sqlx.CREATE)
                throw new DBException("42000", "EOF").Mix();
            PProcedure pp = null;
            var pr = cx.db.GetProcedure(n.ident, arity);
            if (pr == null)
            {
                if (create == Sqlx.CREATE)
                {
                    var np = cx.db.nextPos;
                    pp = new PProcedure(n.ident, ps, rdt, 
                        new Ident(new string(lxr.input,st,lxr.input.Length-st), n.iix,
                        Sqlx.PROCEDURE), np, cx);
                    if (!Match(Sqlx.EOF, Sqlx.SEMICOLON))
                    {
                        // we temporarily place a bodyless version of the procedure in the context
                        pr = new Procedure(pp, cx, new BTree<long, object>(Basis.Name, pp.name));
                        var old = cx;
                        cx.Frame(); // begin compiled object
                        cx.Add(pr);
                        cx.AddProc(pr);
                        cx.AddParams(pr);
                        var op = cx.db.parse;
                        cx.db += (Database._ExecuteStatus, ExecuteStatus.Parse);
                        if (pr is Method mt)
                            cx.AddDefs(mt.udType, cx.db);
                        pp.body = cx.Add(ParseProcedureStatement(pr.defpos)).defpos;
                        pr += (Procedure.Body, pp.body);
                        cx.Add(pr);
                        cx.db += (Database._ExecuteStatus, op);
                        pp.Frame(cx);
                        cx = old;
                        // truncate the source
                        pp.source = new Ident(new string(lxr.input, st, lxr.pos - st), st, Sqlx.PROCEDURE);
                        cx.Add(pp);
                    }
                    cx.db += pr;
                    if (cx.dbformat<51)// provide for possible recursive reference
                        cx.digest += (n.iix, (n.ident, n.iix));
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
                var nn = new Ident(lxr,Sqlx.STATEMENT);
                Mustbe(Sqlx.ID);
                cx.db += pr;
            }
            else if (create == Sqlx.ALTER && (StartMetadata(Sqlx.FUNCTION) || Match(Sqlx.ALTER, Sqlx.ADD, Sqlx.DROP)))
                ParseAlterBody(pr);
            else
            {
                var sq = 1;
                if (ParseMetadata(pr.defpos, sq, Sqlx.FUNCTION, false) is Metadata md)
                    new PMetadata3(n.ident, md.seq, pr.defpos, md.description,
                        md.iri, pr.defpos, md.flags, cx.db.nextPos, cx);
                var s = new string(lxr.input, st, lxr.start - st);
                if (create == Sqlx.CREATE)
                    cx.db += pr;
                cx.defs += (n, pr);
                if (pp != null)
                {
                    pp.source = new Ident(s,n.iix,Sqlx.PROCEDURE);
                    if (cx.db.format < 51)
                        pp.digested = cx.digest;
                }
                else
                    cx.Add(new Modify(n.ident, pr.defpos, s, pr, cx.db.nextPos, cx)); // finally add the Modify
            }
            cx.val = null;
            return (Executable)cx.Add(cr);
        }
        internal void ParseProcedureHeading(PProcedure pp)
        {
            if (tok != Sqlx.LPAREN)
                return;
            var pn = new Ident(pp.name, pp.defpos,Sqlx.PROCEDURE);
            pp.ins = ParseParameters(pn);
            if (tok == Sqlx.RETURNS)
            {
                Next();
                pp.retType = ParseSqlDataType();
            }
        }
        /// <summary>
        /// Function metadata is used to control display of output from table-valued functions
        /// </summary>
        /// <param name="pr"></param>
        Procedure ParseAlterBody(Procedure pr)
        {
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            if (pr.domain.prim!=Sqlx.Null)
                return pr;
            var dt = pr.domain;
            var ri = cx.db.role.infos[dt.defpos] as ObInfo;
            if (ri.Length==0)
                return pr;
            pr = (Procedure)ParseAlterOp(pr);
            while (tok == Sqlx.COMMA)
            {
                Next();
                pr = (Procedure)ParseAlterOp(pr);
            }
            return (Procedure)cx.Add(tr, pr.defpos);
        }
        /// <summary>
        /// Parse a parameter list
        /// </summary>
        /// <returns>the list of formal parameters</returns>
		RowType ParseParameters(Ident pn)
		{
            Mustbe(Sqlx.LPAREN);
            var r = RowType.Empty;
            var ac = cx as CalledActivation;
            var svs = ac?.locals;
			while (tok!=Sqlx.RPAREN)
			{
                var p = ParseFormalParameter(pn);
                cx.Add(p);
                r+= (p.defpos,p.domain);
				if (tok!=Sqlx.COMMA)
					break;
				Next();
			}
            if (ac != null)
                ac.locals = svs;
			Mustbe(Sqlx.RPAREN);
			return r;
		}
        /// <summary>
        /// Parse the return clause 
        /// </summary>
        /// <returns>the data type required (Domain or ObInfo)</returns>
		Domain ParseReturnsClause(Ident pn)
		{
			if (tok!=Sqlx.RETURNS)
				return Domain.Null;
			Next();
            if (tok == Sqlx.ID)
            {
                var s = lxr.val.ToString();
                Next();
                var ob = (ObInfo)cx.db.objects[cx.db.role.dbobjects[s]];
                return ob.domain;
            }
            var st = lxr.pos;
            var lp = lxr.Position;
			var dm = ParseSqlDataType(pn);
            if (lp==dm.defpos && cx.dbformat < 51)
                cx.digest += (lp, (new string(lxr.input,st,lxr.start - st), dm.defpos));
            return dm;
		}
        /// <summary>
        /// parse a formal parameter
        /// </summary>
        /// <returns>the procparameter</returns>
		FormalParameter ParseFormalParameter(Ident pn)
		{
			Sqlx pmode = Sqlx.NONE;
			if (Match(Sqlx.IN,Sqlx.OUT,Sqlx.INOUT))
			{
				pmode = tok;
				Next();
			}
			var n = new Ident(lxr,Sqlx.PARAMETER);
			Mustbe(Sqlx.ID);
            var p = new FormalParameter(lxr.Position, pmode,n.ident, ParseSqlDataType(n));
            cx.Add(p);
            if (pn!=null)
                cx.defs += (new Ident(pn, n), p);
			if (Match(Sqlx.RESULT))
			{
                p += (FormalParameter.Result, true);
				Next();
			}
            cx.Add(p);
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
			Next();
 			if (Match(Sqlx.CONTINUE,Sqlx.EXIT,Sqlx.UNDO))
				return (Executable)cx.Add(ParseHandlerDeclaration());
			var n = new Ident(lxr,Sqlx.HANDLER);
			Mustbe(Sqlx.ID);
            LocalVariableDec lv = null;
            if (tok == Sqlx.CURSOR)
            {
                Next();
                Mustbe(Sqlx.FOR);
                var e = ParseCursorSpecification(-1,Domain.Null.defpos) as SelectStatement;
                lv = new CursorDeclaration(lxr.Position, cx, n.ident, 
                    (CursorSpecification)cx.obs[e.cs]);
            }
            else
            {
                var ld = ParseSqlDataType();
                var vb = new SqlValue(n.iix, n.ident, ld);
                cx.Add(vb);
                lv = new LocalVariableDec(lxr.Position, cx, vb);
                if (Match(Sqlx.EQL, Sqlx.DEFAULT))
                {
                    Next();
                    var iv = ParseSqlValue(-1,ld.defpos);
                    lv += (LocalVariableDec.Init, iv);
                }
            }
            cx.defs += (n, lv);
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
            var hs = new HandlerStatement(lxr.Position, tok, new Ident(lxr,Sqlx.HANDLER).ident);
            Mustbe(Sqlx.CONTINUE, Sqlx.EXIT, Sqlx.UNDO);
            Mustbe(Sqlx.HANDLER);
            Mustbe(Sqlx.FOR);
            var ac = new Activation(cx,hs.name);
            hs+=(HandlerStatement.Conds,ParseConditionValueList());
            var a = ParseProcedureStatement(Domain.Content.defpos);
            hs= hs+(HandlerStatement.Action,a)+(DBObject.Dependents,a.dependents);
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
                switch (n.ToString().Substring(0,2))
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
        /// <returns>the Executable result of the parse</returns>
		Executable ParseCompoundStatement(long xp,string n)
        {
            var cs = new CompoundStatement(lxr.Position, n);
            BList<long> r = BList<long>.Empty;
            Mustbe(Sqlx.BEGIN);
            var ac = new Activation(cx,cs.label);
            if (tok == Sqlx.TRANSACTION)
                throw new DBException("25001", "Nested transactions are not supported").ISO();
            if (tok == Sqlx.XMLNAMESPACES)
            {
                Next();
                r+=cx.Add(ParseXmlNamespaces()).defpos;
            }
            while (tok != Sqlx.END)
            {
                r+=cx.Add(ParseProcedureStatement(xp)).defpos;
                if (tok == Sqlx.END)
                    break;
                Mustbe(Sqlx.SEMICOLON);
            }
            Mustbe(Sqlx.END);
            cs+=(CompoundStatement.Stms,r);
            cs += (DBObject.Dependents, _Deps(r));
            return (Executable)cx.Add(cs);
        }
        internal (Database,Executable) ParseProcedureStatement(string sql,long xp)
        {
            lxr = new Lexer(sql, cx.db.lexeroffset);
            tok = lxr.tok;
            if (lxr.Position > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
            var b = ParseProcedureStatement(xp);
            return (cx.db,(b!=null)?(Executable)cx.Add(b):null);
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
        /// <param name="p">the procedure return type</param>
        /// <returns>the Executable resulting from the parse</returns>
		Executable ParseProcedureStatement(long xp)
		{
            Match(Sqlx.BREAK);
            Executable r;
 			switch (tok)
			{
                case Sqlx.ID: r= ParseLabelledStatement(xp); break;
                case Sqlx.BEGIN: r = ParseCompoundStatement(xp, ""); break;
                case Sqlx.CALL: r = ParseCallStatement(xp); break;
                case Sqlx.CASE: r = ParseCaseStatement(xp); break;
                case Sqlx.CLOSE: r = ParseCloseStatement(); break;
                case Sqlx.COMMIT: throw new DBException("2D000").ISO(); // COMMIT not allowed inside SQL routine
                case Sqlx.BREAK: r = ParseBreakLeave(); break;
                case Sqlx.DECLARE: r = ParseDeclaration(); break;
                case Sqlx.DELETE: r = ParseSqlDelete(); break;
                case Sqlx.EOF: r = new CompoundStatement(lxr.Position,""); break; // okay if it's a partially defined method
                case Sqlx.FETCH: r = ParseFetchStatement(); break;
                case Sqlx.FOR: r = ParseForStatement(xp, null); break;
                case Sqlx.GET: r = ParseGetDiagnosticsStatement(xp); break;
                case Sqlx.HTTP: r = ParseHttpREST(xp); break;
                case Sqlx.IF: r = ParseIfThenElse(xp); break;
                case Sqlx.INSERT: (cx,r) = ParseSqlInsert(xp); break;
                case Sqlx.ITERATE: r = ParseIterate(); break;
                case Sqlx.LEAVE: r = ParseBreakLeave(); break;
                case Sqlx.LOOP: r = ParseLoopStatement(xp, null); break;
                case Sqlx.OPEN: r = ParseOpenStatement(); break;
                case Sqlx.REPEAT: r = ParseRepeat(xp, null); break;
                case Sqlx.ROLLBACK: r = new Executable(lxr.Position, Executable.Type.RollbackWork); break;
                case Sqlx.RETURN: r = ParseReturn(xp); break;
                case Sqlx.SELECT: r = ParseSelectSingle(xp); break;
                case Sqlx.SET: r = ParseAssignment(xp); break;
                case Sqlx.SIGNAL: r = ParseSignal(); break;
                case Sqlx.RESIGNAL: r = ParseSignal(); break;
                case Sqlx.UPDATE: (cx,r) = ParseSqlUpdate(); break;
                case Sqlx.WHILE: r = ParseSqlWhile(xp, null); break;
				default: throw new DBException("42000",lxr.Diag).ISO();
			}
            return (Executable)cx.Add(r);
		}
        /// <summary>
        /// GetDiagnostics = GET DIAGMOSTICS Target = ItemName { , Target = ItemName }
        /// </summary>
        /// <returns>The executable</returns>
        private Executable ParseGetDiagnosticsStatement(long qp)
        {
            Next();
            Mustbe(Sqlx.DIAGNOSTICS);
            var r = new GetDiagnostics(lxr.Position);
            for (; ; )
            {
                var t = ParseSqlValueEntry(qp,Domain.Content.defpos, false);
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
            return (Executable)cx.Add(r);
        }
        /// <summary>
        /// Label =	[ label ':' ] .
        /// Some procedure statements have optional labels. We deal with these here
        /// </summary>
        /// <param name="rt">the expected data type if any</param>
        /// <returns>the Executable resulting from the parse</returns>
		Executable ParseLabelledStatement(long xp)
        {
            Ident sc = new Ident(lxr,Sqlx.STATEMENT);
            var lp = lxr.Position;
            Mustbe(Sqlx.ID);
            // OOPS: according to SQL 2003 there MUST follow a colon for a labelled statement
            if (tok == Sqlx.COLON)
            {
                Next();
                var s = sc.ident;
                switch (tok)
                {
                    case Sqlx.BEGIN: return (Executable)cx.Add(ParseCompoundStatement(xp,s));
                    case Sqlx.FOR: return (Executable)cx.Add(ParseForStatement(xp,s));
                    case Sqlx.LOOP: return (Executable)cx.Add(ParseLoopStatement(xp,s));
                    case Sqlx.REPEAT: return (Executable)cx.Add(ParseRepeat(xp,s));
                    case Sqlx.WHILE: return (Executable)cx.Add(ParseSqlWhile(xp,s));
                    default:
                        throw new DBException("26000", s).ISO();
                }
            }
            // OOPS: but we'q better allow a procedure call here for backwards compatibility
            else if (tok == Sqlx.LPAREN)
            {
                Next();
                var cp = lxr.Position;
                var ps = ParseSqlValueList(xp,Domain.Content.defpos);
                Mustbe(Sqlx.RPAREN);
                var arity = ps.Length;
                var pr = cx.db.GetProcedure(sc.ident, arity) ??
                    throw new DBException("42108", sc.ident+"$"+arity);
                var cs = new CallStatement(cp,pr,sc.ident,ps);
                return (Executable)cx.Add(cs);
            }
            // OOPS: and a simple assignment for backwards compatibility
            else
            {
                var vb = (SqlValue)cx.Get(sc,xp);
                Mustbe(Sqlx.EQL);
                var va = ParseSqlValue(-1,xp);
                var sa = new AssignmentStatement(lp,vb,va);
                return (Executable)cx.Add(sa);
            }
        }
        /// <summary>
		/// Assignment = 	SET Target '='  TypedValue { ',' Target '='  TypedValue }
		/// 	|	SET '(' Target { ',' Target } ')' '='  TypedValue .
        /// </summary>
        /// <returns>The executable resulting from the parse</returns>
		Executable ParseAssignment(long xp)
        {
            var lp = lxr.Position;
            Next();
            if (tok == Sqlx.LPAREN)
                return (Executable)cx.Add(ParseMultipleAssignment(xp));
            var vb = ParseVarOrColumn(xp,Domain.Content.defpos);
            cx.Add(vb);
            Mustbe(Sqlx.EQL);
            var va = ParseSqlValue(xp,vb.defpos);
            var sa = new AssignmentStatement(lp,vb,va);
            return (Executable)cx.Add(sa);
        }
        /// <summary>
        /// 	|	SET '(' Target { ',' Target } ')' '='  TypedValue .
        /// Target = 	id { '.' id } .
        /// </summary>
        /// <returns>the Executable resulting from the parse</returns>
		Executable ParseMultipleAssignment(long xp)
        {
            var ma = new MultipleAssignment(lxr.Position);
            Mustbe(Sqlx.EQL);
            ma = ma + (MultipleAssignment.List,ParseIDList(Sqlx.VALUE))
                +(MultipleAssignment.Rhs,ParseSqlValue(xp,Domain.Content.defpos));
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction)
                cx = ma.Obey(cx);
            return (Executable)cx.Add(ma);
        }
        /// <summary>
        /// |	RETURN TypedValue
        /// </summary>
        /// <returns>the executable resulting from the parse</returns>
		Executable ParseReturn(long xp)
        {
            var rs = new ReturnStatement(lxr.Position);
            Next();
            rs += (ReturnStatement.Ret, cx.Add(ParseSqlValue(xp,xp)).defpos);
            return (Executable)cx.Add(rs);
        }
        /// <summary>
		/// CaseStatement = 	CASE TypedValue { WHEN TypedValue THEN Statements }+ [ ELSE Statements ] END CASE
		/// |	CASE { WHEN SqlValue THEN Statements }+ [ ELSE Statements ] END CASE .
        /// </summary>
        /// <returns>the Executable resulting from the parse</returns>
		Executable ParseCaseStatement(long xp)
        {
            var old = cx;
            Next();
            Executable e;
            if (tok == Sqlx.WHEN)
            {
                e = new SearchedCaseStatement(lxr.Position);
                e+=(SimpleCaseStatement.Whens, ParseWhenList(xp));
                if (tok == Sqlx.ELSE)
                {
                    Next();
                    e+= (SimpleCaseStatement.Else,ParseStatementList(xp));
                }
                Mustbe(Sqlx.END);
                Mustbe(Sqlx.CASE);
            }
            else
            {
                var op = ParseSqlValue(xp,Domain.Content.defpos);
                e = new SimpleCaseStatement(lxr.Position);
                e+=(SimpleCaseStatement.Operand,op);
                e+=(SimpleCaseStatement.Whens,ParseWhenList(op.defpos));
                if (tok == Sqlx.ELSE)
                {
                    Next();
                    e +=(SimpleCaseStatement.Else,ParseStatementList(xp));
                }
                Mustbe(Sqlx.END);
                Mustbe(Sqlx.CASE);
            }
            cx = old;
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                cx = tr.Execute(e,cx);
            return (Executable)cx.Add(e);
        }
        /// <summary>
        /// { WHEN SqlValue THEN Statements }
        /// </summary>
        /// <returns>the list of Whenparts</returns>
		BList<WhenPart> ParseWhenList(long xp)
		{
            var r = BList<WhenPart>.Empty;
			while (tok==Sqlx.WHEN)
			{
				Next();
                var c = ParseSqlValue(xp,xp);
				Mustbe(Sqlx.THEN);
                r+=new WhenPart(lxr.Position, c, ParseStatementList(xp));
			}
			return r;
		}
        /// <summary>
		/// ForStatement =	Label FOR [ For_id AS ][ id CURSOR FOR ] QueryExpression DO Statements END FOR [Label_id] .
        /// </summary>
        /// <param name="n">the label</param>
        /// <param name="rt">The data type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseForStatement(long xp,string n)
        {
            var fs = new ForSelectStatement(lxr.Position, n);
            var old = cx;
            Next();
            Ident c = null;
            if (tok != Sqlx.SELECT)
            {
                c = new Ident(lxr,Sqlx.CURSOR);
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
                        fs+=(ForSelectStatement.ForVn,c);
                        c = new Ident(lxr,Sqlx.CURSOR);
                        Mustbe(Sqlx.ID);
                        Mustbe(Sqlx.CURSOR);
                        Mustbe(Sqlx.FOR);
                    }
                }
            }
            fs = fs + (ForSelectStatement.Sel, 
                ParseCursorSpecification(-1,Domain.Null.defpos).cs)
            + (ForSelectStatement.Cursor, (c ?? new Ident(lxr,Sqlx.CURSOR)).ident);
            Mustbe(Sqlx.DO);
            fs += (ForSelectStatement.Stms,ParseStatementList(xp));
            Mustbe(Sqlx.END);
            Mustbe(Sqlx.FOR);
            if (tok == Sqlx.ID)
            {
                if (n != null && n != lxr.val.ToString())
                    throw new DBException("42157", lxr.val.ToString(), n).Mix();
                Next();
            }
            cx = old;
            return (Executable)cx.Add(fs);
        }
        /// <summary>
		/// IfStatement = 	IF BooleanExpr THEN Statements { ELSEIF BooleanExpr THEN Statements } [ ELSE Statements ] END IF .
        /// </summary>
        /// <param name="rt">The data type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseIfThenElse(long xp)
        {
            var ife = new IfThenElse(lxr.Position);
            var old = cx;
            Next();
            ife += (IfThenElse.Search, cx.Add(ParseSqlValue(-1,Domain.Bool.defpos)).defpos);
            Mustbe(Sqlx.THEN);
            ife += (IfThenElse.Then, ParseStatementList(xp));
            var ei = BList<long>.Empty;
            while (Match(Sqlx.ELSEIF))
            {
                var eif = new IfThenElse(lxr.Position);
                Next();
                eif += (IfThenElse.Search, cx.Add(ParseSqlValue(-1,Domain.Bool.defpos)).defpos);
                Mustbe(Sqlx.THEN);
                Next();
                eif += (IfThenElse.Then, ParseStatementList(xp));
                ei += eif.defpos;
            }
            ife += (IfThenElse.Elsif, ei);
            var el = BList<long>.Empty;
            if (tok == Sqlx.ELSE)
            {
                Next();
                el = ParseStatementList(xp);
            }
            Mustbe(Sqlx.END);
            Mustbe(Sqlx.IF);
            ife += (IfThenElse.Else, el);
            cx = old;
            return (Executable)cx.Add(ife);
        }
        /// <summary>
		/// Statements = 	Statement { ';' Statement } .
        /// </summary>
        /// <param name="rt">The data type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
		BList<long> ParseStatementList(long xp)
		{
            var r = new BList<long>(cx.Add(ParseProcedureStatement(xp)).defpos);
            while (tok==Sqlx.SEMICOLON)
			{
				Next();
                r+=cx.Add(ParseProcedureStatement(xp)).defpos;
			}
			return r;
		}
        /// <summary>
		/// SelectSingle =	[DINSTINCT] SelectList INTO VariableRef { ',' VariableRef } TableExpression .
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseSelectSingle(long xp)
        {
            Next();
            var os = cx.stars;
            var ol = cx.selectLength;
            cx.stars = BList<(int,long)>.Empty;
            var lp = lxr.Position;
            var ss = new SelectSingle(lxr.Position);
            var cs = new CursorSpecification(lp);
            var qe = new QueryExpression(lp+1);
            var s = new QuerySpecification(lp+2,cx,xp)
                + (QueryExpression._Distinct, ParseDistinctClause());
            cs = cs._Union(qe);
            s = ParseSelectList(s,xp);
            s += (Query.Display,s.Size(cx));
            qe+=(QueryExpression._Left,cx.Add(s).defpos);
            Mustbe(Sqlx.INTO);
            ss += (SelectSingle.Outs,ParseTargetList(s.defpos));
            if (ss.outs.Count != s.rowType.Length)
                throw new DBException("22007").ISO();
            s+=(QuerySpecification.TableExp,ParseTableExpression(s).defpos);
            s += (DBObject.Depth, Math.Max(s.depth, 1 + s.tableExp.depth));
            cx.Add(qe);
            cx.Add(s);
            ss+=(ForSelectStatement.Sel,cs);
            cx.stars = os;
            cx.selectLength = ol;
            return (Executable)cx.Add(ss);
        }
        /// <summary>
        /// traverse a comma-separated variable list
        /// </summary>
        /// <returns>the list</returns>
		BList<SqlValue> ParseTargetList(long qp)
		{
			bool b = (tok==Sqlx.LPAREN);
                if (b)
                    Next();
            var r = BList<SqlValue>.Empty;
            for (; ; )
            {
                r += ParseVarOrColumn(qp,Domain.Content.defpos);
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
        SqlValue ParseVarOrColumn(long qp,long xp)
        {
            Match(Sqlx.PROVENANCE, Sqlx.TYPE_URI, Sqlx.SYSTEM_TIME, Sqlx.SECURITY);
            if (tok == Sqlx.SECURITY)
            {
                Next();
                return (SqlValue)cx.Add(new SqlFunction(lxr.Position, cx, Sqlx.SECURITY, null, null, null, Sqlx.NO));
            }
            Ident ic = ParseIdentChain();
            var pseudoTok = Sqlx.NO;
            if (Match(Sqlx.PARTITION, Sqlx.POSITION, Sqlx.VERSIONING, Sqlx.CHECK, 
                Sqlx.PROVENANCE, Sqlx.TYPE_URI, Sqlx.SYSTEM_TIME))
            {
                pseudoTok = tok;
                Next();
            }
            var lp = lxr.Position;
            var dp = ic.iix;
            if (tok == Sqlx.LPAREN)
            {
                Next();
                var ps = RowType.Empty;
                if (tok!=Sqlx.RPAREN)
                    ps = ParseSqlValueList(qp,xp);
                Mustbe(Sqlx.RPAREN);
                int n = ps.Length;
                var pn = (ic.sub != null) ? ic.sub.ident : ic.ident;
                var pr = cx.db.GetProcedure(pn, n);
                var tg = cx.defs[(ic, 1)].Item1;
                if (ic.sub==null)
                {
                    if (pr == null && cx.db.objects[cx.db.role.dbobjects[pn]] is Domain ut
                        && cx.db.role.infos[ut.defpos] is ObInfo ui)
                    {
                        if (cx.db.objects[ui.methods[pn]?[n] ?? -1L] is Method me)
                            return new SqlConstructor(lp, cx, ut,
                                new CallStatement(lp, me, pn, ps, (SqlValue)cx.obs[tg]));
                        if (ut.Length == n)
                            return new SqlDefaultConstructor(lp, cx, ut, ps);
                    }
                    if (pr == null && tg == -1L)
                        throw new DBException("42108", ic.ident);
                    var cs = new CallStatement(lp, pr, pn, ps, cx.obs[tg] as SqlValue);
                    cx.Add(cs);
                    return (SqlValue)cx.Add(new SqlProcedureCall(ic.iix, cx, cs));
                }
                return new SqlMethodCall(ic.iix,cx,
                    new CallStatement(lp,null,pn,ps,(SqlValue)cx.obs[tg]));
            }
            SqlValue r = null;
            if (ic != null)
            {
                if (ic.Length > 0 && cx.defs.Contains(ic.ToString()) 
                    && cx.obs[cx.defs[ic.ToString()].Item1] is SqlValue s0
                    && ((cx.obs[qp] is QuerySpecification qs)?qs.scope.Contains(s0.defpos):true))
                    return Reify(s0,ic);
                var r0 = cx.obs[dp];
                r = r0 as SqlValue;
                if (r == null || r.domain.prim == Sqlx.CONTENT)
                {
                    for (var i = ic.Length; i > 0; i--)
                    {
                        if (ic.ident != "" && char.IsDigit(ic.ident[0])) // cx.dbformat<51 compatibility
                        {
                            var o1 = (DBObject)cx.db.objects[long.Parse(ic.ident)];
                            r = o1.ToSql(ic, cx.db);
                            break;
                        }
                        var (p, k, _, id) = cx.defs[(ic, i)];
                        var ob = cx.obs[p];
                        if (ob is SqlValue sv
                            && ((cx.obs[qp] is QuerySpecification q0) ? q0.scope.Contains(sv.defpos)
                                : true))
                            return Dotted(sv, id, dp);
                        if (ob is TableColumn tc) // I doubt this should happen
                            return new SqlCopy(ic.iix, cx, ic.ident, xp /* !! */, tc.defpos);
                        if (ob is FormalParameter fp)
                            return Dotted(fp,id,dp);
                        if (ob is LocalVariableDec ld)
                            return Dotted(new SqlValue(ld.vbl,ld.name,ld.domain),id,dp);
                    }
                    if (r?.from >= 0 && r.from < Transaction.Analysing && r.defpos > Transaction.Analysing)
                    {
                        if (cx.obs[r.from] != null && r is SqlCopy cp) // replace it with one that has a from
                        {
                            var nc = new SqlCopy(ic.iix, cx, ic.ident, r.from,  cp);
                            r = (SqlValue)cx.Replace(r, nc);
                            cx.defs += (new Ident(ic.ToString(), ic.iix, Sqlx.COLUMN), r);
                        }
                    }
                    r = Dotted(r,ic,dp);
                    cx.defs += (ic, r);
                }
                r = Reify(r,ic);
            }
            if (pseudoTok != Sqlx.NO)
                r = new SqlFunction(lxr.Position, cx, pseudoTok, r, null, null, Sqlx.NO);
            return (SqlValue)cx.Add(r);
        }
        SqlValue Reify(SqlValue sv,Ident ic)
        {
            var r = sv;
            if (r == null)
                r = new SqlValue(ic.iix);
            else
                r = r.Reify(cx, ic);
            if (r == sv)
                return sv;
            if (sv != null)
                r = (SqlValue)cx.Replace(sv, r);
            else
            {
                cx.defs += (new Ident(ic.ToString(), ic.iix, sv.kind),r);
                r = (SqlValue)cx.Add(r);
            }
            return r;
        }
        /// <summary>
        /// Created dotted expression if needed
        /// </summary>
        /// <param name="s">the current left part</param>
        /// <param name="ic">the next part of the chain</param>
        /// <param name="dp">the right defpos for a new dotted SqlValueExpr if required</param>
        /// <returns></returns>
        SqlValue Dotted(SqlValue s,Ident ic,long dp)
        {
            if (ic == null)
                return Reify(s,new Ident(s.name,dp,s.kind));
            for (var b = s?.rowType?.First(); b != null; b = b.Next())
                if (cx.obs[b.value().Item1] is SqlValue c && c.name==ic.ident)
                    return new SqlValueExpr(dp, cx, Sqlx.DOT, 
                        s, Dotted(c, ic.sub,ic.iix-1), Sqlx.NO) + (DBObject._From,s.from);
            var nv = new SqlValue(ic);
            cx.Add(nv);
            if (s == null)
                return Dotted(nv, ic.sub,ic.iix);
            var n = s.name;
            var p = s.defpos + n.Length;
            return new SqlValueExpr(p, cx, Sqlx.DOT, s, Dotted(nv, ic.sub,ic.iix),Sqlx.NO)
                +(DBObject._From,s.from);
        }
        /// <summary>
        /// Parse an identifier
        /// </summary>
        /// <returns>the Ident</returns>
        Ident ParseIdent(Sqlx k)
        {
            var c = new Ident(lxr,k);
            Mustbe(Sqlx.ID);
            return c;
        }
        /// <summary>
        /// Parse a IdentChain
        /// </summary>
        /// <returns>the Ident</returns>
		Ident ParseIdentChain() 
		{
            var left = ParseIdent(Sqlx.NONE);
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
        /// <param name="rt">The data type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
		Executable ParseLoopStatement(long xp, string n)
        {
            var ls = new LoopStatement(lxr.Position, n,cx.cxid);
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
        /// <param name="rt">The data type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseSqlWhile(long xp, string n)
        {
            var ws = new WhileStatement(lxr.Position, n);
            var old = cx; // new SaveContext(lxr, ExecuteStatus.Parse);
            Next();
            ws+=(WhileStatement.Search,ParseSqlValue(-1,Domain.Bool.defpos));
            Mustbe(Sqlx.DO);
            ws+=(WhileStatement.What,ParseStatementList(xp));
            Mustbe(Sqlx.END);
            Mustbe(Sqlx.WHILE);
            if (tok == Sqlx.ID && n != null && n == lxr.val.ToString())
                Next();
            cx = old; // old.Restore(lxr);
            return (Executable)cx.Add(ws);
        }
        /// <summary>
		/// Repeat =		Label REPEAT Statements UNTIL BooleanExpr END REPEAT .
        /// </summary>
        /// <param name="n">the label</param>
        /// <param name="rt">The data type of the test expression</param>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseRepeat(long xp, string n)
        {
            var rs = new RepeatStatement(lxr.Position, n);
            Next();
            rs+=(WhileStatement.What,ParseStatementList(xp));
            Mustbe(Sqlx.UNTIL);
            rs+=(WhileStatement.Search,ParseSqlValue(-1,Domain.Bool.defpos));
            Mustbe(Sqlx.END);
            Mustbe(Sqlx.REPEAT);
            if (tok == Sqlx.ID && n != null && n == lxr.val.ToString())
                Next();
            return (Executable)cx.Add(rs);
        }
        /// <summary>
        /// Parse a break or leave statement
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseBreakLeave()
		{
			Sqlx s = tok;
			Ident n = null;
			Next();
			if (s==Sqlx.LEAVE && tok==Sqlx.ID)
			{
				n = new Ident(lxr,Sqlx.STATEMENT);
				Next();
			}
			return (Executable)cx.Add(new BreakStatement(lxr.Position,n.ident)); 
		}
        /// <summary>
        /// Parse an iterate statement
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
        Executable ParseIterate()
		{
			Next();
			var n = new Ident(lxr,Sqlx.STATEMENT);
			Mustbe(Sqlx.ID);
			return (Executable)cx.Add(new IterateStatement(lxr.Position, n.ident,cx.cxid)); 
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
            var r = new Signal(lxr.Position, n.ToString()) + (Signal.SType, s);
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
                    r+=(Signal.SetList, r.setlist+(k, ParseSqlValue(-1,Domain.Content.defpos).defpos));
                    if (tok != Sqlx.COMMA)
                        break;
                    Next();
                }
            }
            return (Executable)cx.Add(r);
        }
        /// <summary>
		/// Open =		OPEN id .
        /// </summary>
        /// <returns>the Executable result of the parse</returns>
 		Executable ParseOpenStatement()
		{
			Next();
			var o = new Ident(lxr,Sqlx.CURSOR);
			Mustbe(Sqlx.ID);
			return (Executable)cx.Add(new OpenStatement(lxr.Position,
                cx.Get(o, Domain.TableType.defpos) as SqlCursor
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
			var o = new Ident(lxr,Sqlx.CURSOR);
			Mustbe(Sqlx.ID);
			return (Executable)cx.Add(new CloseStatement(lxr.Position,
                cx.Get(o, Domain.TableType.defpos) as SqlCursor
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
            var how = Sqlx.NEXT;
            SqlValue where = null;
            if (Match(Sqlx.NEXT, Sqlx.PRIOR, Sqlx.FIRST, Sqlx.LAST, Sqlx.ABSOLUTE, Sqlx.RELATIVE))
            {
                how = tok;
                Next();
            }
            if (how == Sqlx.ABSOLUTE || how == Sqlx.RELATIVE)
                where = ParseSqlValue(-1,Domain.Int.defpos);
            if (tok == Sqlx.FROM)
                Next();
            var o = new Ident(lxr,Sqlx.CURSOR);
            Mustbe(Sqlx.ID);
            var fs = new FetchStatement(lxr.Position, 
                cx.Get(o, Domain.TableType.defpos) as SqlCursor 
                ?? throw new DBException("34000", o.ToString()),
                how, where);
            Mustbe(Sqlx.INTO);
            fs+=(FetchStatement.Outs,ParseTargetList(-1));
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
            cx.parseStart = lxr.Position;
            var os = cx.nextStmt;
            var oi = (ObInfo)cx.role.infos[trig.target];
            var fn = new Ident(oi.name, cx.parseStart+1,Sqlx.TRIGGER);
            var tb = (Table)cx.db.objects[trig.target];
            tb = (Table)cx.Add(tb);
            var fm = new From(fn, cx, tb);
            fm = (From)cx.Add(fm);
            tb.AddCols(cx,fn, fm.rowType);
            var tg = new Trigger(trig);
            cx.Add(tg); // incomplete version for parsing
            if (trig.oldTable != null)
            {
                var tt = (TransitionTable)cx.Add(new TransitionTable(trig.oldTable, true, cx, fm, tg));
                cx.defs += (trig.oldTable,tt);
            }
            if (trig.oldRow != null)
            {
                var sr = (SqlOldRow)cx.Add(new SqlOldRow(trig.oldRow, cx, trig, fm));
                cx.defs += (trig.oldRow, sr);
            }
            if (trig.newTable != null)
            {
                var tt = (TransitionTable)cx.Add(new TransitionTable(trig.newTable, true, cx, fm, tg));
                cx.defs += (trig.newTable, tt);
            }
            if (trig.newRow != null)
            {
                var sr = (SqlNewRow)cx.Add(new SqlNewRow(trig.newRow, cx, trig, fm));
                cx.defs += (trig.newRow, sr);
            }
            SqlValue when = SqlNull.Value;
            Executable act;
            if (tok == Sqlx.WHEN)
            {
                Next();
                when = ParseSqlValue(-1,Domain.Bool.defpos);
            }
            if (tok == Sqlx.BEGIN)
            {
                Next();
                var cs = new CompoundStatement(lxr.Position,"");
                var ss = BList<long>.Empty;
                Mustbe(Sqlx.ATOMIC);
                while (tok != Sqlx.END)
                {
                    ss+=cx.Add(ParseProcedureStatement(Domain.Content.defpos)).defpos; 
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
                act = ParseProcedureStatement(Domain.Content.defpos);
            cx.Add(act);
            var r = (WhenPart)cx.Add(new WhenPart(lxr.Position, when, new BList<long>(act.defpos)));
            trig.def = r.defpos;
            cx.parseStart = oldStart;
            cx.nextStmt = os;
            return r.defpos;
        }
        /// <summary>
        /// |	CREATE TRIGGER id (BEFORE|AFTER) Event ON id [ RefObj ] Trigger
        /// RefObj = REFERENCING  { (OLD|NEW)[ROW|TABLE][AS] id } .
        /// Trigger = FOR EACH ROW ...
        /// </summary>
        /// <returns>the executable</returns>
        Executable ParseTriggerDefClause()
        {
            var ct = new Executable(lxr.Position, Executable.Type.CreateTrigger);
            var trig = new Ident(lxr,Sqlx.TRIGGER);
            Mustbe(Sqlx.ID);
            PTrigger.TrigType tgtype = 0;
            var w = Mustbe(Sqlx.BEFORE, Sqlx.INSTEAD, Sqlx.AFTER);
            switch (w)
            {
                case Sqlx.BEFORE: tgtype |= PTrigger.TrigType.Before; break;
                case Sqlx.AFTER: tgtype |= PTrigger.TrigType.After; break;
                case Sqlx.INSTEAD: Mustbe(Sqlx.OF); tgtype |= PTrigger.TrigType.Instead; break;
            }
            var qp = lxr.Position;
            tgtype = ParseTriggerHow(tgtype);
            var cls = BList<Ident>.Empty;
            var upd = (tgtype & PTrigger.TrigType.Update) == PTrigger.TrigType.Update;
            if (upd && tok == Sqlx.OF)
            {
                Next();
                cls += new Ident(lxr,Sqlx.COLUMN);
                Mustbe(Sqlx.ID);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    cls += new Ident(lxr,Sqlx.COLUMN);
                    Mustbe(Sqlx.ID);
                }
            }
            Mustbe(Sqlx.ON);
            var tabl = new Ident(lxr,Sqlx.TABLE);
            Mustbe(Sqlx.ID);
            var tb = cx.db.GetObject(tabl.ident) as Table ??
                throw new DBException("42107", tabl.ToString()).Mix();
            Ident or = null, nr = null, ot = null, nt = null;
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
                            ot = new Ident(lxr,Sqlx.TABLE);
                            Mustbe(Sqlx.ID);
                            continue;
                        }
                        if (tok == Sqlx.ROW)
                            Next();
                        if (or != null)
                            throw new DBException("42143", "OLD ROW").Mix();
                        if (tok == Sqlx.AS)
                            Next();
                        or = new Ident(lxr,Sqlx.ROW);
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
                            nt = new Ident(lxr.val.ToString(), tabl.iix,Sqlx.TABLE);
                            Mustbe(Sqlx.ID);
                            continue;
                        }
                        if (tok == Sqlx.ROW)
                            Next();
                        if (nr != null)
                            throw new DBException("42143", "NEW ROW").Mix();
                        if (tok == Sqlx.AS)
                            Next();
                        nr = new Ident(lxr.val.ToString(), tabl.iix,Sqlx.TABLE);
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
            var old = cx; // new SaveContext(lxr, ExecuteStatus.Parse);
            var st = lxr.start;
            var cols = (cls!=BList<Ident>.Empty)?BList<long>.Empty:null;
            for (int i = 0; i < cls.Length; i++)
                cols += cx.defs[cls[i]].Item1;
            var np = cx.db.nextPos;
            var pt = new PTrigger(trig.ident, tb.defpos, (int)tgtype, cols,
                    or, nr, ot, nt, new Ident(new string(lxr.input, st, lxr.input.Length - st), st,
                    Sqlx.TRIGGER),
                    cx, np);
            var op = cx.db.parse;
            cx.db += (Database._ExecuteStatus, ExecuteStatus.Parse);
            var lp = lxr.Position;
            cx.Frame();
            ParseTriggerDefinition(pt);
            pt.Frame(cx);
            pt.src = new Ident(new string(lxr.input, st, lxr.pos - st), lp, Sqlx.TRIGGER);
            cx.db += (Database._ExecuteStatus, op);
            cx = old;
            cx.Add(pt);
            return (Executable)cx.Add(ct);
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
		Executable ParseAlter()
		{
			Next();
            MethodModes();
            Match(Sqlx.DOMAIN,Sqlx.TYPE,Sqlx.ROLE,Sqlx.VIEW);
			switch (tok)
			{
				case Sqlx.TABLE: return ParseAlterTable();
				case Sqlx.DOMAIN: return ParseAlterDomain();
				case Sqlx.TYPE: return ParseAlterType();
				case Sqlx.FUNCTION: return ParseAlterProcedure();
				case Sqlx.PROCEDURE: return ParseAlterProcedure();
				case Sqlx.OVERRIDING: return ParseMethodDefinition();
				case Sqlx.INSTANCE: return ParseMethodDefinition();
				case Sqlx.STATIC: return ParseMethodDefinition();
				case Sqlx.CONSTRUCTOR: return ParseMethodDefinition();
				case Sqlx.METHOD: return ParseMethodDefinition();
                case Sqlx.VIEW: return ParseAlterView();
				default:
					throw new DBException("42125",tok).Mix();
			}
		}
        /// <summary>
        /// id AlterTable { ',' AlterTable } 
        /// </summary>
        /// <returns>the Executable</returns>
        Executable ParseAlterTable()
        {
            var at = new Executable(lxr.Position, Executable.Type.AlterTable);
            Next();
            Table tb;
            var o = new Ident(lxr,Sqlx.TABLE);
            Mustbe(Sqlx.ID);
            tb = cx.db.GetObject(o.ident) as Table??
                throw new DBException("42107", o).Mix();
            ParseAlterTableOps(tb);
            return (Executable)cx.Add(at);
        }
        /// <summary>
        /// AlterView = SET (INSERT|UPDATE|DELETE) SqlStatement
        ///     |   TO id
        ///     |   SET SOURCE To QueryExpression
        ///     |   [DROP]TableMetadata
        /// </summary>
        /// <returns>the executable</returns>
        Executable ParseAlterView()
        {
            var at = new Executable(lxr.Position, Executable.Type.AlterTable);
            Next();
            var nm = new Ident(lxr,Sqlx.TABLE);
            Mustbe(Sqlx.ID);
            DBObject vw = cx.db.GetObject(nm.ident) ??
                throw new DBException("42107", nm).Mix();
            ParseAlterOp(vw);
            while (tok == Sqlx.COMMA)
            {
                Next();
                ParseAlterOp(vw);
            }
            return (Executable)cx.Add(at);
        }
        /// <summary>
        /// Parse an alter operation
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="ob">the object to be affected</param>
        DBObject ParseAlterOp(DBObject ob)
        {
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            var kind = (ob is View)?Sqlx.VIEW:Sqlx.PROCEDURE;
            var oi = cx.db.role.infos[ob.defpos] as ObInfo;
            long np;
            if (tok == Sqlx.SET && ob is View)
            {
                Next();
                int st;
                string s;
                if (Match(Sqlx.SOURCE))
                {
                    Next();
                    Mustbe(Sqlx.TO);
                    st = lxr.start;
                    var qe = ParseQueryExpression(-1,ob.defpos);
                    s = new string(lxr.input,st, lxr.start - st);
                    np = tr.nextPos;
                    cx.Add(new Modify("Source", ob.defpos, s, qe, np, cx));
                    return cx.Add(tr,ob.defpos);
                }
                var t = tok;
                Mustbe(Sqlx.INSERT, Sqlx.UPDATE, Sqlx.DELETE);
                Mustbe(Sqlx.TO);
                st = lxr.start;
                ParseSqlStatement(Domain.Content.defpos);
                s = new string(lxr.input,st, lxr.start - st);
                string n = "";
                switch (t)
                {
                    case Sqlx.INSERT: n = "Insert"; break;
                    case Sqlx.UPDATE: n = "Update"; break;
                    case Sqlx.DELETE: n = "Delete"; break;
                }
                np = tr.nextPos;
                cx.Add(new Modify(n, ob.defpos, s, null, np, cx));
            }
            else if (tok == Sqlx.TO)
            {
                Next();
                var nm = lxr.val;
                Mustbe(Sqlx.ID);
                np = tr.nextPos;
                cx.Add(new Change(ob.defpos, nm.ToString(), np, cx));
            }
            else if (tok == Sqlx.ALTER)
            {
                Next();
                var ic = new Ident(lxr,Sqlx.COLUMN);
                Mustbe(Sqlx.ID);
                ob = cx.db.GetObject(ic.ident) ??
                    throw new DBException("42135", ic.ident);
                    if (cx.tr.role.Denied(cx, Grant.Privilege.AdminRole))
                        throw new DBException("42105", cx.db.role.name??"").Mix();
                var m = ParseMetadata(ob.defpos, -1, Sqlx.COLUMN);
                np = tr.nextPos;
                cx.Add(new PMetadata(ic.ident, m, ob.defpos, np, cx));
            }
            if (StartMetadata(kind) || Match(Sqlx.ADD))
            {
                if (tr.role.Denied(cx, Grant.Privilege.AdminRole))
                    throw new DBException("42105", cx.db.role.name ?? "").Mix();
                if (tok == Sqlx.ALTER)
                    Next();
                var m = ParseMetadata(ob.defpos, -1, kind);
                cx.Add(new PMetadata(oi.name, m, -1, ob.defpos, cx));
            }
            return cx.Add(tr, ob.defpos);
        }
        /// <summary>
        /// id AlterDomain { ',' AlterDomain } 
        /// </summary>
        /// <returns>the Executable</returns>
        Executable ParseAlterDomain()
        {
            var ad = new Executable(lxr.Position, Executable.Type.AlterDomain);
                Next();
                var c = ParseIdent(Sqlx.TYPE);
                var d = cx.db.GetObInfo(c.ident).domain;
                ParseAlterDomainOp(d);
                while (tok == Sqlx.COMMA)
                {
                    Next();
                    ParseAlterDomainOp(d);
                }
            return (Executable)cx.Add(ad);
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
            var oi = cx.db.role.infos[d.defpos] as ObInfo;
            if (tok == Sqlx.SET)
			{
				Next();
				Mustbe(Sqlx.DEFAULT);
				int st = lxr.start;
				var dv = ParseSqlValue(d.defpos,Domain.For(d.prim).defpos);
				string ds = new string(lxr.input,st,lxr.start-st);
                if (cx.db.parse == ExecuteStatus.Obey)
                    cx.Add(new Edit(d, oi.name, d + (Domain.Default, dv) + (Domain.DefaultString, ds),
                        cx.db.nextPos, cx));
			} 
			else if (Match(Sqlx.ADD))
			{
				Next();
                Ident id;
                if (tok == Sqlx.CONSTRAINT)
                {
                    Next();
                    id = new Ident(lxr,Sqlx.CHECK);
                    Mustbe(Sqlx.ID);
                }
                else 
                    id = new Ident(lxr,Sqlx.CHECK);
				Mustbe(Sqlx.CHECK);
				Mustbe(Sqlx.LPAREN);
                int st = lxr.pos;
                var sc = ParseSqlValue(d.defpos,Domain.Bool.defpos).Reify(cx);
                string source = new string(lxr.input,st, lxr.pos - st - 1);
				Mustbe(Sqlx.RPAREN);
                if (cx.tr.parse == ExecuteStatus.Obey)
                {
                    var pc = new PCheck(d.defpos, id.ident, sc, source, cx.tr.nextPos, cx);
                    pc.Frame(cx);
                    cx.Add(pc);
                }
                cx.frame = null;
			}
            else if (tok == Sqlx.DROP)
            {
                Next();
                if (tok == Sqlx.DEFAULT)
                {
                    Next();
                    if (tr.parse == ExecuteStatus.Obey)
                        cx.Add(new Edit(d, oi.name, d, tr.nextPos, cx));
                }
                else if (StartMetadata(Sqlx.DOMAIN) || Match(Sqlx.ADD, Sqlx.DROP))
                {
                    if (tr.role == null || 
                        tr.role.Denied(cx, Grant.Privilege.AdminRole))
                        throw new DBException("42105", tr.role.name ?? "").Mix();
                   cx.Add(new PMetadata(oi.name, ParseMetadata(d.defpos, -1, Sqlx.DOMAIN), -1, 
                        d.defpos, cx));
                }
                else
                {
                    Mustbe(Sqlx.CONSTRAINT);
                    var n = new Ident(lxr,Sqlx.VALUE);
                    Mustbe(Sqlx.ID);
                    Drop.DropAction s = ParseDropAction();
                    var ch = cx.db.GetObject(n.ident) as Check;
                    if (cx.db.parse == ExecuteStatus.Obey)
                        cx.Add(new Drop1(ch.defpos, s, cx.tr.nextPos, cx));
                }
            }
            else if (StartMetadata(Sqlx.DOMAIN) || Match(Sqlx.ADD, Sqlx.DROP))
            {
                if (tr.role.Denied(cx, Grant.Privilege.AdminRole))
                    throw new DBException("42105", cx.db.role.name??"").Mix();
                cx.Add(new PMetadata(oi.name,ParseMetadata(d.defpos, -1, Sqlx.DOMAIN), 
                    tr.nextPos, d.defpos, cx));
            }
            else
            {
                Mustbe(Sqlx.TYPE);
                var dt = ParseSqlDataType();
                if (tr.parse == ExecuteStatus.Obey)
                    cx.Add(new Edit(d, oi.name, dt, tr.nextPos, cx));
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
        BTree<long,ObInfo> ParseAlterTable(Table tb,BTree<long,ObInfo>ns = null)
        {
            ns = ns ?? BTree<long, ObInfo>.Empty;
            var tr = cx.db as Transaction?? throw new DBException("2F003");
            if (tok == Sqlx.TO)
            {
                Next();
                var o = lxr.val;
                var lp = lxr.Position;
                Mustbe(Sqlx.ID);
                if (tr.parse == ExecuteStatus.Obey)
                    cx.Add(new Change(tb.defpos, o.ToString(), cx.tr.nextPos, cx));
                return ns;
            } 
            if (tok==Sqlx.LEVEL)
                return ParseClassification(tb.defpos, ns);
            if (tok==Sqlx.SCOPE)
                return ParseEnforcement(tb.defpos,ns);
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
                                    var name = new Ident(lxr,Sqlx.CHECK);
                                    Mustbe(Sqlx.ID);
                                    Drop.DropAction act = ParseDropAction();
                                    if (tb != null)
                                    {
                                        var ck = cx.db.GetObject(name.ident) as Check;
                                        if (ck != null && cx.db.parse == ExecuteStatus.Obey)
                                            new Drop1(ck.defpos, act, tr.nextPos, cx);
                                    }
                                    return ns;
                                }
                            case Sqlx.PRIMARY:
                                {
                                    Next();
                                    Mustbe(Sqlx.KEY);
                                    var lp = lxr.Position;
                                    Drop.DropAction act = ParseDropAction();
                                    var cols = ParseColsList(tb);
                                    if (tb != null)
                                    {
                                        Index x = tb.FindIndex(cx.db, cols);
                                        if (x != null && cx.db.parse == ExecuteStatus.Obey)
                                            new Drop1(x.defpos, act, cx.tr.nextPos, cx);
                                        return ns;
                                    }
                                    else
                                        return ns;
                                }
                            case Sqlx.FOREIGN:
                                {
                                    Next();
                                    Mustbe(Sqlx.KEY);
                                    var cols = ParseColsList(tb);
                                    Mustbe(Sqlx.REFERENCES);
                                    var n = new Ident(lxr,Sqlx.TABLE);
                                    Mustbe(Sqlx.ID);
                                    var rt = cx.db.GetObject(n.ident) as Table??
                                        throw new DBException("42107", n).Mix();
                                    var st = lxr.pos;
                                    if (tok == Sqlx.LPAREN)
                                        ParseColsList(rt); //??
                                    if (tr.parse == ExecuteStatus.Obey)
                                    {
                                        var x = tb.FindIndex(cx.db,cols);
                                        if (x != null)
                                        {
                                            cx.Add(new Drop(x.defpos, cx.tr.nextPos,cx));
                                            return ns;
                                        }
                                        throw new DBException("42135", new string(lxr.input, st, lxr.pos)).Mix();
                                    }
                                    else
                                        return ns;
                                }
                            case Sqlx.UNIQUE:
                                {
                                    Next();
                                    var st = lxr.pos;
                                    var lp = lxr.Position;
                                    var cols = ParseColsList(tb);
                                    if (cx.db.parse == ExecuteStatus.Obey)
                                    {
                                        var x = tb.FindIndex(cx.db,cols);
                                        if (x != null)
                                        {
                                            cx.Add(new Drop(x.defpos, cx.tr.nextPos, cx));
                                            return ns;
                                        }
                                        throw new DBException("42135", new string(lxr.input, st, lxr.pos)).Mix();
                                    }
                                    else
                                        return ns;
                                }
                            case Sqlx.PERIOD:
                                {
                                    var lp = lxr.Position;
                                    var ptd = ParseTablePeriodDefinition();
                                    var pd = (ptd.pkind==Sqlx.SYSTEM_TIME)?tb.systemPS:tb.applicationPS;
                                    if (pd > 0)
                                        cx.Add(new Drop(pd, tr.nextPos, cx));
                                    return ns;
                                }
                            case Sqlx.WITH:
                                tb = ParseVersioningClause(tb.defpos, true); return ns;
                            default:
                                {
                                    if (StartMetadata(Sqlx.TABLE))
                                    {
                                        var lp = lxr.Position;
                                        var ti = tr.role.infos[tb.defpos] as ObInfo;
                                        if (tr.role.Denied(cx, Grant.Privilege.AdminRole))
                                            throw new DBException("42105", cx.db.role.name ?? "").Mix();
                                        cx.Add(new PMetadata(ti.name, 
                                            ParseMetadata(tb.defpos, -1, Sqlx.TABLE,true), 
                                            -1, tb.defpos, cx));
                                        return ns;
                                    }
                                    if (tok == Sqlx.COLUMN)
                                        Next();
                                    var name = new Ident(lxr,Sqlx.COLUMN);
                                    Mustbe(Sqlx.ID);
                                    Drop.DropAction act = ParseDropAction();
                                    if (tb != null)
                                    {
                                        var ft = tr.role.infos[tb.defpos] as ObInfo;
                                        var tc = ft.ColFor(cx,name.ident);
                                        if (tc != null && tr.parse == ExecuteStatus.Obey)
                                        {
                                            cx.Add(new Drop1(tc.defpos, act, cx.tr.nextPos, cx));
                                            return ns;
                                        }
                                    }
                                    break;
                                }
                        }
                        break;
                    }
                case Sqlx.ADD:
                    {
                        Next();
                        if (tok == Sqlx.PERIOD)
                            ns = AddTablePeriodDefinition(tb.defpos, ns);
                        else if (tok == Sqlx.WITH)
                            ParseVersioningClause(tb.defpos, false);
                        else if (tok == Sqlx.CONSTRAINT || tok == Sqlx.UNIQUE || tok == Sqlx.PRIMARY || tok == Sqlx.FOREIGN || tok == Sqlx.CHECK)
                            ns = ParseTableConstraintDefin(tb.defpos, ns);
                        else
                        {
                            ns = ParseColumnDefin(tb.defpos, ns);
                            if (cx.tr.physicals.Last()?.value() is PColumn pc 
                                && pc.notNull && tb.tableRows.PositionAt(0) != null)
                                    throw new DBException("42130");
                        }
                        break;
                    }
                case Sqlx.ALTER:
                    {
                        Next();
                        if (tok == Sqlx.COLUMN)
                            Next();
                        ObInfo col = null;
                        var o = new Ident(lxr,Sqlx.COLUMN);
                        Mustbe(Sqlx.ID);
                        if (tb != null)
                        {
                            var ft = cx.db.role.infos[tb.defpos] as ObInfo;
                            col = ft.ColFor(cx,o.ident);
                        }
                        if (col == null)
                            throw new DBException("42112", o.ident).Mix();
                        while (StartMetadata(Sqlx.COLUMN) || Match(Sqlx.TO,Sqlx.POSITION,Sqlx.SET,Sqlx.DROP,Sqlx.ADD,Sqlx.TYPE))
                            ParseAlterColumn(tb, (TableColumn)cx.db.objects[col.defpos], col.name);
                        break;
                    }
                case Sqlx.PERIOD:
                    {
                        if (Match(Sqlx.ID))
                        {
                            var pid = lxr.val;
                            Next();
                            Mustbe(Sqlx.TO);
                            var pd = cx.db.objects[tb.applicationPS] as PeriodDef;
                            if (pd == null)
                                throw new DBException("42162", pid).Mix();
                            pid = lxr.val;
                            var lp = lxr.Position;
                            Mustbe(Sqlx.ID);
                            if (cx.db.parse == ExecuteStatus.Obey)
                                cx.Add(new Change(pd.defpos, pid.ToString(), cx.tr.nextPos, cx));
                        }
                        ns = AddTablePeriodDefinition(tb.defpos,ns);
                        break;
                    }
                case Sqlx.SET:
                    {
                        Next();
                        var cols = ParseColsList(tb);
                        Mustbe(Sqlx.REFERENCES);
                        var n = new Ident(lxr,Sqlx.TABLE);
                        Mustbe(Sqlx.ID);
                        var rt = cx.db.GetObject(n.ident) as Table ??
                            throw new DBException("42107", n).Mix();
                        var st = lxr.pos;
                        if (tok == Sqlx.LPAREN)
                            ParseColsList(rt);
                        PIndex.ConstraintType ct=0;
                        if (tok == Sqlx.ON)
                            ct = ParseReferentialAction();
                        if (cx.db.parse == ExecuteStatus.Obey)
                        {
                            var x = tb.FindIndex(cx.db, cols);
                            if (x != null)
                            {
                                cx.Add(new RefAction(x.defpos, ct, tr.nextPos, cx));
                                return ns;
                            }
                            throw new DBException("42135", new string(lxr.input, st, lxr.pos)).Mix();
                        }
                        else
                            return ns;
                    }
                default:
                    if (StartMetadata(Sqlx.TABLE) || Match(Sqlx.ADD, Sqlx.DROP))
                        if (cx.db.parse == ExecuteStatus.Obey)
                        {
                            var oi = tr.role.infos[tb.defpos] as ObInfo;
                            if (tb.Denied(cx, Grant.Privilege.AdminRole))
                                throw new DBException("42015",oi.name);
                            cx.Add(new PMetadata(oi.name, ParseMetadata(tb.defpos, -1, Sqlx.TABLE), 
                                -1, tb.defpos, cx));
                        }
                    break;
            }
            return ns;
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
        BTree<long,ObInfo> ParseAlterColumn(Table tb, TableColumn tc, string nm,BTree<long,ObInfo> ns=null)
		{
            ns = ns ?? BTree<long, ObInfo>.Empty;
            var tr = cx.db as Transaction?? throw new DBException("2F003");
			TypedValue o = null;
            var ti = cx.db.role.infos[tb.defpos] as ObInfo;
            if (tok == Sqlx.TO)
            {
                Next();
                var n = new Ident(lxr,Sqlx.COLUMN);
                Mustbe(Sqlx.ID);
                if (cx.db.parse == ExecuteStatus.Obey)
                    cx.Add(new Change(tc.defpos, n.ident, tr.nextPos, cx));
                return ns;
            }
            if (tok == Sqlx.POSITION)
            {
                Next();
                o = lxr.val;
                var lp = lxr.Position;
                Mustbe(Sqlx.INTEGERLITERAL);
                if (cx.db.parse == ExecuteStatus.Obey)
                    new Alter3(tc.defpos, nm, o.ToInt().Value, tc.tabledefpos,
                        tc.domain.defpos,
                        tc.generated.gfs ?? tc.domain.defaultValue?.ToString() ?? "",
                        tc.domain.defaultValue,
                        "", tc.update, tc.notNull, tc.generated, cx.tr.nextPos, cx);
                return ns;
            }
            if (Match(Sqlx.ADD))
            {
                Next();
                if (StartMetadata(Sqlx.COLUMN))
                {
                    var lp = lxr.Position;
                    if (tb.Denied(cx, Grant.Privilege.AdminRole))
                        throw new DBException("42105", ti.name).Mix();
                    cx.Add(new PMetadata(nm, 
                        ParseMetadata(tc.defpos, -1, Sqlx.COLUMN, false), 0, tc.defpos, cx));
                    return ns;
                }
                if (tok == Sqlx.CONSTRAINT)
                    Next();
                var n = new Ident(lxr,Sqlx.CHECK);
                Mustbe(Sqlx.ID);
                Mustbe(Sqlx.CHECK);
                Mustbe(Sqlx.LPAREN);
                int st = lxr.pos;
                var se = ParseSqlValue(tb.defpos,Domain.Bool.defpos).Reify(cx);
                string source = new string(lxr.input,st, lxr.pos - st - 1);
                Mustbe(Sqlx.RPAREN);
                if (cx.db.parse == ExecuteStatus.Obey)
                {
                    var pc = new PCheck(tc.defpos, n.ident, se, source, cx.tr.nextPos, cx);
                    pc.Frame(cx);
                    cx.Add(pc);
                }
                return ns;
            }
            if (tok == Sqlx.SET)
            {
                Next();
                if (tok == Sqlx.DEFAULT)
                {
                    Next();
                    int st = lxr.start;
                    var lp = lxr.Position;
                    var dv = lxr.val;
                    Next();
                    var ds = new string(lxr.input, st, lxr.start - st);
                    if (cx.db.parse == ExecuteStatus.Obey)
                        cx.Add(new Alter3(tc.defpos, nm, ti.PosFor(cx,nm), tc.tabledefpos,
                            tc.domain.defpos, ds, dv, "",
                            BList<UpdateAssignment>.Empty, tc.notNull, GenerationRule.None,
                            cx.tr.nextPos, cx));
                    return ns;
                }
                if (Match(Sqlx.GENERATED))
                {
                    Domain type = null;
                    var lp = lxr.Position;
                    var oc = cx;
                    cx = cx.ForConstraintParse(ns);
                    var gr = ParseGenerationRule(tc.defpos) + (DBObject.Framing, cx.obs);
                    cx = oc;
                    if (cx.db.parse == ExecuteStatus.Obey)
                    {
                        tc.ColumnCheck(tr, true);
                        new Alter3(tc.defpos, nm, ti.PosFor(cx,nm), tc.tabledefpos, 
                            tc.domain.defpos,
                            gr.gfs??type.defaultValue?.ToString()??"",
                            type.defaultValue,"",BList<UpdateAssignment>.Empty, tc.notNull, 
                            gr, cx.tr.nextPos, cx);
                    }
                    return ns;
                }
                if (Match(Sqlx.NOT))
                {
                    Next();
                    var lp = lxr.Position;
                    Mustbe(Sqlx.NULL);
                    if (cx.db.parse == ExecuteStatus.Obey)
                    {
                        tc.ColumnCheck(tr, false);
                        new Alter3(tc.defpos, nm, ti.PosFor(cx,nm), tc.tabledefpos, tc.domain.defpos,
                            "",TNull.Value, "", BList<UpdateAssignment>.Empty, true, 
                            tc.generated, cx.tr.nextPos, cx);
                    }
                    return ns;
                }
                ns = ParseColumnConstraint(tb.defpos, tc.defpos, ns);
                return ns;
            }
            else if (tok == Sqlx.DROP)
            {
                Next();
                if (StartMetadata(Sqlx.COLUMN))
                {
                    var lp = lxr.Position;
                    if (tb.Denied(cx, Grant.Privilege.AdminRole))
                        throw new DBException("42105", ti.name ?? "").Mix();
                    new PMetadata(nm, 
                            ParseMetadata(tc.defpos, -1, Sqlx.COLUMN, true), 0,
                        tc.defpos, cx);
                    return ns;
                }
                if (tok != Sqlx.DEFAULT && tok != Sqlx.NOT && tok != Sqlx.PRIMARY && tok != Sqlx.REFERENCES && tok != Sqlx.UNIQUE && tok != Sqlx.CONSTRAINT && !StartMetadata(Sqlx.COLUMN))
                    throw new DBException("42000", lxr.Diag).ISO();
                if (tok == Sqlx.DEFAULT)
                {
                    var lp = lxr.Position;
                    Next();
                    if (cx.db.parse == ExecuteStatus.Obey)
                        new Alter3(tc.defpos, nm, ti.PosFor(cx,nm), tc.tabledefpos,
                            tc.domain.defpos, "", null, tc.updateString, tc.update, tc.notNull,
                            GenerationRule.None, cx.tr.nextPos, cx);
                    return ns;
                }
                else if (tok == Sqlx.NOT)
                {
                    Next();
                    var lp = lxr.Position;
                    Mustbe(Sqlx.NULL);
                    if (cx.db.parse == ExecuteStatus.Obey)
                        new Alter3(tc.defpos, nm, ti.PosFor(cx,nm), tc.tabledefpos, tc.domain.defpos,
                            tc.domain.defaultString, tc.domain.defaultValue,
                            tc.updateString, tc.update, false,
                            tc.generated, cx.tr.nextPos, cx);
                    return ns;
                }
                else if (tok == Sqlx.PRIMARY)
                {
                    Next();
                    var lp = lxr.Position;
                    Mustbe(Sqlx.KEY);
                    Drop.DropAction act = ParseDropAction();
                    if (cx.db.parse == ExecuteStatus.Obey)
                    {
                        Index x = tb.FindPrimaryIndex(cx.db);
                        if (x.keys.Count != 1 
                            || x.keys[0].Item1 != tc.defpos)
                            throw new DBException("42158", nm, ti.name).Mix()
                                .Add(Sqlx.TABLE_NAME,new TChar(ti.name))
                                .Add(Sqlx.COLUMN_NAME,new TChar(nm));
                        new Drop1(x.defpos, act, tr.nextPos, cx);
                    }
                    return ns;
                }
                else if (tok == Sqlx.REFERENCES)
                {
                    Next();
                    var n = new Ident(lxr,Sqlx.TABLE);
                    Mustbe(Sqlx.ID);
                    Ident k = null;
                    if (tok == Sqlx.LPAREN)
                    {
                        Next();
                        k = new Ident(lxr,Sqlx.COLUMN);
                        Mustbe(Sqlx.ID);
                        Mustbe(Sqlx.RPAREN);
                    }
                    Index dx = null;
                    Table rt = null;
                    ObInfo ri = null;
                    for (var p = tb.indexes.First();p!= null;p=p.Next())
                    {
                        var x = (Index)cx.db.objects[p.value()];
                        if (x.keys.Count != 1 || x.keys[0].Item1 != tc.defpos)
                            continue;
                        rt = cx.db.objects[x.reftabledefpos] as Table;
                        ri = cx.db.role.infos[rt.defpos] as ObInfo;
                        if (ri.name == n.ident)
                        {
                            dx = x;
                            break;
                        }
                    }
                    if (dx == null)
                        throw new DBException("42159", nm, n.ident).Mix()
                            .Add(Sqlx.TABLE_NAME,new TChar(n.ident))
                            .Add(Sqlx.COLUMN_NAME,new TChar(nm));
                    if (cx.db.parse == ExecuteStatus.Obey)
                        cx.Add(new Drop(dx.defpos, tr.nextPos, cx));
                    return ns;
                }
                else if (tok == Sqlx.UNIQUE)
                {
                    var lp = lxr.Position;
                    Next();
                    Index dx = null;
                    for (var p =tb.indexes.First();p!= null;p=p.Next())
                    {
                        var x = (Index)cx.db.objects[p.value()];
                        if (x.keys.Count != 1 
                            || x.keys[0].Item1 != tc.defpos)
                            continue;
                        if ((x.flags & PIndex.ConstraintType.Unique) == PIndex.ConstraintType.Unique)
                        {
                            dx = x;
                            break;
                        }
                    }
                    if (dx == null)
                        throw new DBException("42160", nm).Mix()
                            .Add(Sqlx.TABLE_NAME,new TChar(nm));
                    if (cx.db.parse == ExecuteStatus.Obey)
                        new Drop(dx.defpos, cx.tr.nextPos, cx);
                    return ns;
                }
                else if (tok == Sqlx.CONSTRAINT)
                {
                    var n = new Ident(lxr,Sqlx.CHECK);
                    Mustbe(Sqlx.ID);
                    Drop.DropAction s = ParseDropAction();
                    var ch = cx.db.GetObject(n.ident) as Check;
                    if (cx.db.parse == ExecuteStatus.Obey)
                        new Drop1(ch.defpos, s, tr.nextPos, cx);
                    return ns;
                }
            }
            else if (Match(Sqlx.TYPE))
            {
                Next();
                long dm = -1L;
                var lp = lxr.Position;
                Domain type = null;
                if (tok == Sqlx.ID)
                {
                    var domain = new Ident(lxr,Sqlx.TYPE);
                    Next();
                    type = cx.db.GetObInfo(domain.ident).domain;
                    if (type == null)
                        throw new DBException("42119", domain.ident, cx.db.name).Mix()
                            .Add(Sqlx.CATALOG_NAME, new TChar(cx.db.name))
                            .Add(Sqlx.TYPE, new TChar(domain.ident));
                    dm = type.defpos;
                }
                else if (Match(Sqlx.CHARACTER, Sqlx.CHAR, Sqlx.VARCHAR, Sqlx.NATIONAL, Sqlx.NCHAR,
                    Sqlx.BOOLEAN, Sqlx.NUMERIC, Sqlx.DECIMAL,
                    Sqlx.DEC, Sqlx.FLOAT, Sqlx.REAL, // Sqlx.LONG,Sqlx.DOUBLE,
                    Sqlx.INT, // Sqlx.BIGINT,
                    Sqlx.INTEGER,// Sqlx.SMALLINT,
                    Sqlx.BINARY, Sqlx.BLOB, Sqlx.NCLOB,
                    Sqlx.CLOB, Sqlx.DATE, Sqlx.TIME, Sqlx.ROW, Sqlx.TABLE))
                {
                    type = ParseSqlDataType()+(Domain.Default,tc.domain.defaultValue)
                        +(Domain.DefaultString,tc.domain.defaultString);
                    type = Domain.Create(cx,type);
                    dm = type.defpos;
                }
                if (cx.db.parse == ExecuteStatus.Obey)
                {
                    if (!tc.domain.CanTakeValueOf(cx,type))
                        throw new DBException("2200G");
                    new Alter3(tc.defpos, nm, ti.PosFor(cx,nm), tc.tabledefpos, dm, 
                        type.defaultString,type.defaultValue,tc.updateString, tc.update,
                        tc.notNull, tc.generated, tr.nextPos, cx);
                }
                return ns;
            }
            if (StartMetadata(Sqlx.COLUMN))
            {
                var lp = lxr.Position;
                if (tb.Denied(cx, Grant.Privilege.AdminRole))
                    throw new DBException("42105", ti.name ?? "").Mix();
                var md = ParseMetadata(tc.defpos, -1, Sqlx.COLUMN);
                new PMetadata(nm, md, tc.defpos, tr.nextPos, cx);
            }
            return ns;
		}
        /// <summary>
		/// AlterType = TO id
        ///     |   ADD ( Member | Method )
		/// 	|	SET Member_id To id
		/// 	|	DROP ( Member_id | (MethodType Method_id '('Type{','Type}')')) DropAction
		/// 	|	ALTER Member_id AlterMember { ',' AlterMember } .
        /// </summary>
        /// <returns>the executable</returns>
        Executable ParseAlterType()
        {
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            var at = new Executable(lxr.Position, Executable.Type.AlterType);
            Next();
            var id = new Ident(lxr,Sqlx.TYPE);
            Mustbe(Sqlx.ID);
            var tp = cx.db.GetObject(id.ident) as Domain??
                throw new DBException("42133", id.ident).Mix()
                    .Add(Sqlx.TYPE, new TChar(id.ident));
            var ti = cx.db.role.infos[tp.defpos] as ObInfo;
            if (tok == Sqlx.TO)
            {
                Next();
                id = new Ident(lxr,Sqlx.TYPE);
                Mustbe(Sqlx.ID);
                if (cx.db.parse == ExecuteStatus.Obey)
                    cx.Add(new Change(tp.defpos, id.ident, cx.tr.nextPos, cx));
            }
            else if (tok == Sqlx.SET)
            {
                id = new Ident(lxr,Sqlx.COLUMN);
                Mustbe(Sqlx.ID);
                var sq = ti.PosFor(cx,id.ident);
                var ts = ti.ColFor(cx,id.ident);
                var tc = cx.db.objects[ts?.defpos ?? -1L] as TableColumn;
                if (tc == null)
                    throw new DBException("42133", id).Mix()
                        .Add(Sqlx.TYPE, new TChar(id.ident));
                Mustbe(Sqlx.TO);
                id = new Ident(lxr,Sqlx.COLUMN);
                Mustbe(Sqlx.ID);
                if (cx.db.parse == ExecuteStatus.Obey)
                    new Alter3(tc.defpos, id.ident, sq, tc.tabledefpos,
                        tc.domain.defpos, tc.domain.defaultString,
                        tc.domain.defaultValue, tc.updateString,
                        tc.update, tc.notNull, tc.generated, cx.tr.nextPos, cx);
            }
            else if (tok == Sqlx.DROP)
            {
                Next();
                id = new Ident(lxr,Sqlx.PROCEDURE);
                if (MethodModes())
                {
                    MethodName mn = ParseMethod(Domain.Null.defpos);
                    var ui = (ObInfo)cx.db.role.infos[tp.defpos];
                    Method mt = cx.db.objects[ui.methods[mn.mname.ident]?[mn.arity]??-1L] as Method;
                    if (mt == null)
                        throw new DBException("42133", tp).Mix().
                            Add(Sqlx.TYPE, new TChar(ti.name));
                    if (cx.db.parse == ExecuteStatus.Obey)
                    {
                        ParseDropAction();
                        new Drop(mt.defpos, cx.tr.nextPos, cx);
                    }
                }
                else
                {
                    var tc = (TableColumn)cx.db.objects[ti.ColFor(cx,id.ident).defpos];
                    if (tc == null)
                        throw new DBException("42133", id).Mix()
                            .Add(Sqlx.TYPE, new TChar(id.ident));
                    if (cx.db.parse == ExecuteStatus.Obey)
                    {
                        ParseDropAction();
                        new Drop(tc.defpos, tr.nextPos, cx);
                    }
                }
            }
            else if (Match(Sqlx.ADD))
            {
                Next();
                MethodModes();
                if (Match(Sqlx.INSTANCE, Sqlx.STATIC, Sqlx.CONSTRUCTOR, Sqlx.OVERRIDING, Sqlx.METHOD))
                {
                    ParseMethodHeader(tp.defpos);
                    return cx.exec;
                }
                var nc = ti.Length;
                var (cn,dm) = ParseMember(id);
                tp += (nc, dm);
                if (tp.structure.Length > 0 && cx.db.parse == ExecuteStatus.Obey)
                    cx.Add(new PColumn2(cn.iix, cn.ident, nc,
                        dm.defpos, dm.defaultString, dm.defaultValue,
                        false, GenerationRule.None, cx.tr.nextPos, cx));
            }
            else if (tok == Sqlx.ALTER)
            {
                Next();
                id = new Ident(lxr,Sqlx.COLUMN);
                Mustbe(Sqlx.ID);
                var tc = ti.ColFor(cx,id.ident);
                if (tc == null)
                    throw new DBException("42133", id).Mix()
                        .Add(Sqlx.TYPE, new TChar(id.ident));
                ParseAlterMembers(tc);
            }
            return at;
        }
        /// <summary>
        /// AlterMember =	TYPE Type
        /// 	|	SET DEFAULT TypedValue
        /// 	|	DROP DEFAULT .
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="tc">The UDType member</param>
        void ParseAlterMembers(ObInfo tc)
        {
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            TypedValue dv = tc.domain.defaultValue;
            var co = cx.db.objects[tc.defpos] as TableColumn;
            var ti = cx.db.role.infos[co.tabledefpos] as ObInfo;
            var dt = tc.domain;
            var ds = "";
            for (; ; )
            {
                var lp = lxr.Position;
                if (tok == Sqlx.TO)
                {
                    Next();
                    var n = new Ident(lxr,Sqlx.COLUMN);
                    Mustbe(Sqlx.ID);
                    if (cx.db.parse == ExecuteStatus.Obey)
                        cx.Add(new Change(tc.defpos, n.ident, cx.tr.nextPos, cx));
                    goto skip;
                }
                else if (Match(Sqlx.TYPE))
                {
                    Next();
                    dt = Domain.Create(cx, ParseSqlDataType());
                }
                else if (tok == Sqlx.SET)
                {
                    Next();
                    Mustbe(Sqlx.DEFAULT);
                    var st = lxr.start;
                    dv = lxr.val;
                    Next();
                    ds = new string(lxr.input, st, lxr.start - st);
                }
                else if (tok == Sqlx.DROP)
                {
                    Next();
                    Mustbe(Sqlx.DEFAULT);
                    dv = TNull.Value;
                }
                if (cx.db.parse == ExecuteStatus.Obey)
                    new Alter3(tc.defpos, tc.name, ti.PosFor(cx,tc.name), co.tabledefpos,
                         co.domain.defpos, ds, dv, co.updateString, co.update,
                         co.notNull, GenerationRule.None, cx.tr.nextPos, cx);
            skip:
                if (tok != Sqlx.COMMA)
                    break;
                Next();
            }
        }
        /// <summary>
        /// FUNCTION id '(' Parameters ')' RETURNS Type Statement
        /// PROCEDURE id '(' Parameters ')' Statement
        /// </summary>
        /// <returns>the executable</returns>
		Executable ParseAlterProcedure()
        {
            var ar = new Executable(lxr.Position, Executable.Type.AlterRoutine);
            bool func = tok == Sqlx.FUNCTION;
            Next();
            ParseProcedureClause(func, Sqlx.ALTER);
            return ar;
        }
        /// <summary>
		/// DropStatement = 	DROP DropObject DropAction .
		/// DropObject = 	ORDERING FOR id
		/// 	|	ROLE id
		/// 	|	TRIGGER id
		/// 	|	ObjectName .
        /// </summary>
        /// <returns>the executable</returns>
		Executable ParseDropStatement()
        {
            var tr = cx.db as Transaction ?? throw new DBException("2F003");
            Next();
            if (Match(Sqlx.ORDERING))
            {
                var dr = new Executable(lxr.Position, Executable.Type.DropOrdering);
                Next(); Mustbe(Sqlx.FOR);
                var o = new Ident(lxr,Sqlx.TYPE);
                Mustbe(Sqlx.ID);
                ParseDropAction(); // ignore if present
                var tp = cx.db.GetObject(o.ident) as Domain ??
                    throw new DBException("42133", o.ToString()).Mix();
                if (cx.db.parse == ExecuteStatus.Obey)
                    cx.Add(new Ordering(tp.defpos, -1L, OrderCategory.None, tr.nextPos, cx));
                return dr;
            }
            else
            {
                var dt = new Executable(lxr.Position, Executable.Type.DropTable);
                var lp = lxr.Position;
                var (ob, n) = ParseObjectName();
                var a = ParseDropAction();
                if (cx.db.parse == ExecuteStatus.Obey)
                    ob.Cascade(cx,a);
                return dt;
            }
        }
        /// <summary>
		/// Type = 		StandardType | DefinedType | DomainName | REF(TableReference) .
		/// DefinedType = 	ROW  Representation
		/// 	|	TABLE Representation
        /// 	|   ( Type {, Type }) 
        ///     |   Type UNION Type { UNION Type }.
        /// </summary>
        /// <returns>The specified Domain or ObInfo</returns>
		Domain ParseSqlDataType(Ident pn=null)
        {
            Domain r = null;
            Domain oi = Domain.Null;
            Sqlx tp = tok;
            if (Match(Sqlx.TABLE, Sqlx.ROW, Sqlx.TYPE, Sqlx.LPAREN))// anonymous row type
            {
                if (Match(Sqlx.TABLE, Sqlx.ROW, Sqlx.TYPE))
                    Next();
                else
                    tp = Sqlx.TYPE;
                if (tok == Sqlx.LPAREN)
                    return cx.Dom(ParseRowTypeSpec(tp, pn));
            }
            if (Match(Sqlx.REF))
            {
                Next();
                var llp = lxr.Position;
                Mustbe(Sqlx.LPAREN);
                var q = new TableExpression(llp, BTree<long,bool>.Empty,BTree<long, object>.Empty);
                var t = ParseTableReference(null);
                q = q + (TableExpression.Target, t.defpos) 
                    + (DBObject.Dependents, new BTree<long, bool>(t.defpos, true))
                    + (DBObject.Depth, 1 + t.depth);
                Mustbe(Sqlx.RPAREN);
                var np = cx.db.nextPos;
                var nd = Domain.Create(cx,new Domain(np, Sqlx.REF,
                    ((DBObject)cx.db.role.infos[q.defpos]).mem)); 
                return nd;
            }
            r = ParseStandardDataType();
            if (r == null)
            {
                var tr = cx.db as Transaction ?? throw new DBException("2F003");
                var o = new Ident(lxr,Sqlx.TYPE);
                if (tok == Sqlx.TABLE) // ad hoc table type
                {
                    Next();
                    var np = cx.db.nextPos;
                    var pt = new PTable(np, cx);
                    cx.Add(pt);
                    cx.Add(new PDomain(Physical.Type.PDomain, "", Sqlx.TABLE, 0,
                        0, CharSet.UCS, "", "", pt.ppos, np + 1, cx));
                    var ta = (Table)cx.db.objects[pt.ppos];
                    if (pn != null)
                        ta += (Basis.Name, pn.ident);
                    var x = ParseTableContentsSource(ta.defpos);
                    r = (Domain)cx.db.objects[np + 1];
                }
                else
                {
                    Mustbe(Sqlx.ID);
                    var op = cx.db.role.dbobjects[o.ident];
                    if (op < 0)
                        throw new DBException("42119", o.ident, "").Mix();
                    var ut = (Domain)cx.db.objects[op];
                    var ui = (ObInfo)cx.db.role.infos[ut.defpos];
                    r = ut;
                    for (var b = ui.domain.representation.First(); b != null; b = b.Next())
                    {
                        var p = b.key();
                        var sc = cx.obs[p] as SqlValue;
                        if (sc!=null)
                            cx.defs += (new Ident(o, new Ident(sc.name, p, Sqlx.COLUMN)),sc);
                    }
                }
            }
            if (tok == Sqlx.SENSITIVE)
            {
                var np = cx.db.nextPos;
                r = new Domain(np, tok, r);
                Next();
            }
            if (r is Domain dm)
                r = Domain.Create(cx, dm);
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
        /// <returns>the data type</returns>
        Domain ParseStandardDataType()
        {
            Domain r = null;
            Domain r0 = null;
            var lp = lxr.Position;
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
                    var o = new Ident(lxr,Sqlx.NONE);
                    Mustbe(Sqlx.ID);
                    r+=(Domain.Charset,(CharSet)Enum.Parse(typeof(CharSet), o.ident, false));
                }
                if (tok == Sqlx.COLLATE)
                    r+=(Domain.Culture, new CultureInfo(ParseCollate()));
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
            else if (Match(Sqlx.FLOAT, Sqlx.REAL ,Sqlx.DOUBLE))
            {
                r = r0 = Domain.Real;
                if (tok == Sqlx.DOUBLE)
                    Mustbe(Sqlx.PRECISION);
                Next();
                r = ParsePrecPart(r);
            }
            else if (Match(Sqlx.INT, Sqlx.INTEGER ,Sqlx.BIGINT,Sqlx.SMALLINT))
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
            else if (Match(Sqlx.BLOB,Sqlx.XML))
            {
                r = r0 = Domain.Blob;
                Next();
            }
            else if (Match(Sqlx.DATE, Sqlx.TIME, Sqlx.TIMESTAMP, Sqlx.INTERVAL))
            {
                Domain dr = r0 = Domain.Timestamp;
                switch(tok)
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
            else if (Match(Sqlx.OBJECT))
            {
                r = r0 = Domain.ObjectId;
                Next();
            }
            if (r == r0) 
                return r0; // completely standard
            return new Domain(lp,r); // set it to a positive defpos
        }
        /// <summary>
		/// IntervalType = 	INTERVAL IntervalField [ TO IntervalField ] .
		/// IntervalField = 	YEAR | MONTH | DAY | HOUR | MINUTE | SECOND ['(' int ')'] .
        /// </summary>
        /// <param name="q">The Domain being specified</param>
        /// <returns>the modified data type</returns>
        Domain ParseIntervalType()
		{
            var lp = lxr.Position;
			Sqlx start = Mustbe(Sqlx.YEAR,Sqlx.DAY,Sqlx.MONTH,Sqlx.HOUR,Sqlx.MINUTE,Sqlx.SECOND);
            var d = new Domain(lp,Domain.Interval)+(Domain.Start, start);
			if (tok==Sqlx.LPAREN)
			{
				Next();
				var p1 = lxr.val;
				Mustbe(Sqlx.INTEGERLITERAL);
				d+=(Domain.Scale,p1.ToInt().Value);
				if (start==Sqlx.SECOND && tok==Sqlx.COMMA)
				{
					Next();
					var p2 = lxr.val;
					Mustbe(Sqlx.INTEGERLITERAL);
					d+=(Domain.Precision,p2.ToInt().Value);
				}
				Mustbe(Sqlx.RPAREN);
			}
			if (tok==Sqlx.TO)
			{
				Next();
				Sqlx end = Mustbe(Sqlx.YEAR,Sqlx.DAY,Sqlx.MONTH,Sqlx.HOUR,Sqlx.MINUTE,Sqlx.SECOND);
                d += (Domain.End, end);
				if (end==Sqlx.SECOND && tok==Sqlx.LPAREN)
				{
					Next();
					var p2 = lxr.val;
					Mustbe(Sqlx.INTEGERLITERAL);
					d+=(Domain.Precision,p2.ToInt().Value);
					Mustbe(Sqlx.RPAREN);
				}
			}
            return d;
		}
        /// <summary>
        /// Handle ROW type or TABLE type in Type specification
        /// </summary>
        /// <param name="d">The type of domain (TYPE, ROW or TABLE)</param>
        /// <param name="tdp">ref: the typedefpos</param>
        /// <returns>The RowTypeSpec</returns>
        long ParseRowTypeSpec(Sqlx d, Ident pn=null, long under = -1L)
        {
            if (tok == Sqlx.ID)
            {
                var id = new Ident(lxr,Sqlx.TYPE);
                Next();
                var ob = cx.db.GetObject(id.ident) ??
                    throw new DBException("42107", id.ident).Mix();
                return ob.defpos;
            }
            var lp = lxr.Position;
            var ns = BList<string>.Empty;
            var ms = RowType.Empty;
            var lps = BList<long>.Empty;
            var rd = Domain.Row;
            var sl = lxr.start;
            Mustbe(Sqlx.LPAREN);
            for (var n = 0; ; n++)
            {
                lps += lxr.Position;
                var (cn,dm) = ParseMember(pn);
                ns += cn.ident;
                ms += (cn.iix,dm);
                rd += (cn.iix, dm);
                if (tok != Sqlx.COMMA)
                    break;
                Next();
            }
            Mustbe(Sqlx.RPAREN);
            /*            var ic = new Ident(new string(lxr.input, sl, lxr.start - sl),lp,Sqlx.ROW);
                        var pt = new PTable(ic.ident, cx.db.nextPos, cx);
                        cx.Add(pt);
                        var tb = new Table(pt);
                        cx.db += tb;
                        var j = 0;
                        for (var b = ms.First(); b != null; b = b.Next(), j++)
                        {
                            var (p, dm) = b.value();
                            var pc = new PColumn3(pt.defpos, ns[j], j, dm.defpos,
                                "", dm.defaultValue, "", BList<UpdateAssignment>.Empty,
                                false, GenerationRule.None, cx.db.nextPos, cx);
                            cx.Add(pc);
                            var co = new TableColumn(tb, pc, dm);
                            cx.db += co;
                        } 
                        return tb.defpos;
            */
            return cx.Add(new Structure(lp, pn?.ident ?? "", rd, ms, ns)).defpos; 
        }
		(Ident,Domain) ParseMember(Ident pn)
		{
            Ident n = null;
            if (tok == Sqlx.ID)
            {
                n = new Ident(lxr,Sqlx.COLUMN);
                Next();
            }
            var dm = ParseSqlDataType(pn);
			if (tok==Sqlx.DEFAULT)
			{
				int st = lxr.start;
                long lp = lxr.Position;
				ParseSqlValue(dm.defpos,dm.defpos);
				dm += (Domain.Default,dm.Parse(new Scanner(lp,lxr.input,st)));
			}
            if (tok == Sqlx.COLLATE)
                dm+= (Domain.Culture,ParseCollate());
            dm = Domain.Create(cx, dm);
            ParseMetadata(n.iix,-1, Sqlx.COLUMN);
            return (n,dm);
		}
        /// <summary>
        /// Parse a precision
        /// </summary>
        /// <param name="r">The SqldataType</param>
        /// <returns>the updated data type</returns>
		Domain ParsePrecPart(Domain r)
		{
			if (tok==Sqlx.LPAREN)
			{
				Next();
                if (lxr.val is TInt)
                {
                    int prec = lxr.val.ToInt().Value;
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
        /// <returns>the updated data type</returns>
		Domain ParsePrecScale(Domain r)
		{
			if (tok==Sqlx.LPAREN)
			{
				Next();
                if (lxr.val is TInt)
                {
                    int prec = lxr.val.ToInt().Value;
                    r += (Domain.Precision, prec);
                }
				Mustbe(Sqlx.INTEGERLITERAL);
				if (tok==Sqlx.COMMA)
				{
					Next();
                    if (lxr.val is TInt)
                    {
                        int scale = lxr.val.ToInt().Value;
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
		Executable ParseSqlSet()
        {
            Next();
            if (Match(Sqlx.ROLE))
            {
                var sr = new Executable(lxr.Position, Executable.Type.SetRole);
                Next();
                ParseSetRole();
                return (Executable)cx.Add(sr);
            }
            if (Match(Sqlx.AUTHORIZATION))
            {
                var ss = new Executable(lxr.Position, Executable.Type.SetSessionAuthorization);
                Next();
                Mustbe(Sqlx.EQL);
                Mustbe(Sqlx.CURATED);
                var tr = cx.db as Transaction ?? throw new DBException("2F003");
                if (cx.db.user == null || cx.db.user.defpos != cx.db.owner)
                    throw new DBException("42105").Mix();
                var np = cx.db.nextPos;
                if (cx.db.parse == ExecuteStatus.Obey)
                    cx.Add(new Curated(np,cx));
                return (Executable)cx.Add(ss);
            }
            if (Match(Sqlx.PROFILING))
            {
                var sc = new Executable(lxr.Position, Executable.Type.SetSessionCharacteristics);
                Next();
                Mustbe(Sqlx.EQL);
                if (cx.db.user == null || cx.db.user.defpos != cx.db.owner)
                    throw new DBException("42105").Mix();
                var o = lxr.val;
                Mustbe(Sqlx.BOOLEANLITERAL);
                // ignore for now
                return (Executable)cx.Add(sc);
            }
            if (Match(Sqlx.TIMEOUT))
            {
                var sc = new Executable(lxr.Position, Executable.Type.SetSessionCharacteristics);
                Next();
                Mustbe(Sqlx.EQL);
                if (cx.db.user == null || cx.db.user.defpos != cx.db.owner)
                    throw new DBException("42105").Mix();
                var o2 = lxr.val;
                Mustbe(Sqlx.INTEGERLITERAL);
                // ignore for now
                return (Executable)cx.Add(sc);
            }
            // Rename
            Ident n = null;
            Match(Sqlx.DOMAIN, Sqlx.ROLE, Sqlx.VIEW, Sqlx.TYPE);
            MethodModes();
            Sqlx kind = tok;
            DBObject ob = null;
            Executable at = null;
            bool meth = false;
            if (Match(Sqlx.TABLE, Sqlx.DOMAIN, Sqlx.ROLE, Sqlx.VIEW, Sqlx.TYPE))
            {
                var tk = (tok==Sqlx.VIEW)?Sqlx.TABLE:tok;
                Next();
                n = new Ident(lxr,tk);
                Mustbe(Sqlx.ID);
                ob = cx.db.GetObject(n.ident) ??
                    throw new DBException("42135", n.ident);
                switch (kind)
                {
                    case Sqlx.TABLE:
                        at = new Executable(lxr.Position, Executable.Type.AlterTable);
                        break;
                    case Sqlx.DOMAIN:
                        at = new Executable(lxr.Position, Executable.Type.AlterDomain);
                        break;
                    case Sqlx.ROLE:
                        at = new Executable(lxr.Position, Executable.Type.CreateRole);
                        break;
                    case Sqlx.VIEW:
                        at = new Executable(lxr.Position, Executable.Type.AlterTable);
                        break;
                    case Sqlx.TYPE:
                        at = new Executable(lxr.Position, Executable.Type.AlterType);
                        break;
                }
            }
            else
            {
                at = new Executable(lxr.Position, Executable.Type.AlterRoutine);
                meth = false;
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
                n = new Ident(lxr,Sqlx.PROCEDURE);
                var nid = n.ident;
                Mustbe(Sqlx.ID);
                int arity = 0;
                if (tok == Sqlx.LPAREN)
                {
                    Next();
                    ParseSqlDataType();
                    arity++;
                    while (tok == Sqlx.COMMA)
                    {
                        Next();
                        ParseSqlDataType();
                        arity++;
                    }
                    Mustbe(Sqlx.RPAREN);
                }
                if (meth)
                {
                    Ident type = null;
                    if (mt == PMethod.MethodType.Constructor)
                        type = new Ident(nid, 0, Sqlx.PROCEDURE);
                    if (tok == Sqlx.FOR)
                    {
                        Next();
                        type = new Ident(lxr, Sqlx.TYPE);
                        Mustbe(Sqlx.ID);
                    }
                    if (type == null)
                        throw new DBException("42134").Mix();
                    var ut = (Domain)cx.db.GetObject(type.ident);
                    var ui = (ObInfo)cx.db.role.infos[ut?.defpos??-1L];
                    ob = cx.db.objects[ui?.methods[n.ident]?[arity]??-1L] as Method;
                }
                else
                    ob = cx.db.GetProcedure(n.ident,arity);
                if (ob == null)
                    throw new DBException("42135", "Object not found").Mix();
                Mustbe(Sqlx.TO);
                var nm = new Ident(lxr,ob.kind);
                Mustbe(Sqlx.ID);
                if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                    cx.Add(new Change(ob.defpos, nm.ident, tr.nextPos, cx));
            }
            return (Executable)cx.Add(at);
        }
        /// <summary>
        /// 	|	SET ROLE id | NONE
        /// 	This needs to change properties of the connection, so that it applies to the session
        /// </summary>
		void ParseSetRole()
        {
            var n = new Ident(lxr,Sqlx.ROLE);
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.FOR)
            {
                Next();
                Mustbe(Sqlx.DATABASE);
                Mustbe(Sqlx.ID);
            }
            var a = cx.db.GetObject(n.ident) as Role;
            var dm = (a != null) ? (ObInfo)cx.db.role.infos[a.defpos] : null;
            if (dm == null || (dm.priv&Grant.Privilege.UseRole)==0)
                throw new DBException("42135", n.ident);
            cx.db += (Database.Role, a.defpos);
            if (cx.next == null)
                cx = new Context(cx.db);
            else
                cx = new Context(cx, a, cx.user);
        }
        /// <summary>
        /// Start the parse for a Query
        /// </summary>
        /// <param name="sql">The sql to parse</param>
        /// <returns>A CursorSpecification</returns>
		public SelectStatement ParseCursorSpecification(string sql,long xp)
		{
			lxr = new Lexer(sql, cx.db.lexeroffset);
			tok = lxr.tok;
            if (lxr.Position > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId+sql.Length);
			return ParseCursorSpecification(-1,xp);
		}
        /// <summary>
		/// CursorSpecification = [ XMLOption ] QueryExpression  .
        /// </summary>
        /// <returns>A CursorSpecification</returns>
		internal SelectStatement ParseCursorSpecification(long qp,long xp)
        {
            var lp = lxr.Position;
            int st = lxr.start;
            var od = cx.defs; // subquery will have its own idents
            CursorSpecification r = new CursorSpecification(lxr.Position);
            ParseXmlOption(false);
            var qe = ParseQueryExpression(qp,Domain.TableType.defpos);
            if (!cx.Dom(xp).CanTakeValueOf(cx,qe.domain))
                throw new DBException("22000");
            r = r._Union(qe);
            r += (Query.Display, qe.display);
            r += (Query.OrdSpec, qe.ordSpec);
            r += (DBObject._RowType, qe.rowType);
            r += (CursorSpecification._Source,new string(lxr.input, st, lxr.start - st));
            r = (CursorSpecification)cx.Add(r);
            var s = new SelectStatement(lp - 1, r);
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction)
                cx.val = r.RowSets(cx, cx.data[r.from]?.finder??BTree<long, RowSet.Finder>.Empty);
            cx.defs = od; // restore parsing context
            return (SelectStatement)cx.Add(s);
        }
        /// <summary>
        /// Start the parse for a QueryExpression (called from View)
        /// </summary>
        /// <param name="sql">The sql string</param>
        /// <returns>A QueryExpression</returns>
		public QueryExpression ParseQueryExpression(long qp,string sql,long xp)
		{
			lxr = new Lexer(sql);
			tok = lxr.tok;
            if (lxr.Position > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
			return ParseQueryExpression(qp,xp);
		}
        /// <summary>
        /// QueryExpression = QueryExpressionBody [OrderByClause] [FetchFirstClause] .
		/// QueryExpressionBody = QueryTerm 
		/// | QueryExpressionBody ( UNION | EXCEPT ) [ ALL | DISTINCT ] QueryTerm .
		/// A simple table query is defined (SQL2003-02 14.1SR18c) as a CursorSpecification in which the QueryExpression is a QueryTerm that is a QueryPrimary that is a QuerySpecification.
        /// </summary>
        /// <param name="t">the expected data type</param>
        /// <returns>A QueryExpression</returns>
		QueryExpression ParseQueryExpression(long qp,long xp)
        {
            QueryExpression left = ParseQueryTerm(qp,xp);
            while (Match(Sqlx.UNION, Sqlx.EXCEPT))
            {
                Sqlx op = tok;
                var lp = lxr.Position;
                Next();
                Sqlx m = Sqlx.DISTINCT;
                if (Match(Sqlx.ALL, Sqlx.DISTINCT))
                {
                    m = tok;
                    Next();
                }
                left = new QueryExpression(lp + 1, cx, left, op, ParseQueryTerm(xp,xp));
                if (m == Sqlx.DISTINCT)
                    left += (QueryExpression._Distinct, true);
            }
            var ois = left.ordSpec??new OrderSpec(BList<SqlValue>.Empty);
            var nis = ParseOrderClause(left.defpos,ois, left.simpletablequery);
            left = (QueryExpression)cx.obs[left.defpos];
            if (ois != nis)
                left += (Query.OrdSpec, nis);
            cx.Add(left);
            if (Match(Sqlx.FETCH))
            {
                Next();
                Mustbe(Sqlx.FIRST);
                var o = lxr.val;
                if (tok == Sqlx.INTEGERLITERAL)
                {
                    left += (Query.FetchFirst, o.ToInt().Value);
                    Next();
                }
                else
                    left += (Query.FetchFirst, 1);
                Mustbe(Sqlx.ROW, Sqlx.ROWS);
                Mustbe(Sqlx.ONLY);
            }
            return (QueryExpression)cx.Add(left.Refresh(cx));
        }
        /// <summary>
		/// QueryTerm = QueryPrimary | QueryTerm INTERSECT [ ALL | DISTINCT ] QueryPrimary .
        /// A simple table query is defined (SQL2003-02 14.1SR18c) as a CursorSpecification in which the QueryExpression is a QueryTerm that is a QueryPrimary that is a QuerySpecification.
        /// </summary>
        /// <param name="t">the expected data type</param>
        /// <returns>the QueryExpression</returns>
		QueryExpression ParseQueryTerm(long qp,long xp)
		{
			QueryExpression left = ParseQueryPrimary(qp,xp);
			while (Match(Sqlx.INTERSECT))
			{
                var lp = lxr.Position;
				Next();
				Sqlx m = Sqlx.DISTINCT;
				if (Match(Sqlx.ALL,Sqlx.DISTINCT))
				{
					m = tok;
					Next();
				}
				left = new QueryExpression(lp+1, cx, left,Sqlx.INTERSECT,ParseQueryPrimary(qp,xp));
                if (m == Sqlx.DISTINCT)
                    left += (QueryExpression._Distinct, true);
			}
			return (QueryExpression)cx.Add(left);
		}
        /// <summary>
		/// QueryPrimary = QuerySpecification |  TypedValue | TABLE id .
        /// A simple table query is defined (SQL2003-02 14.1SR18c) as a CursorSpecification in which the QueryExpression is a QueryTerm that is a QueryPrimary that is a QuerySpecification.
        /// </summary>
        /// <param name="t">the expected data type</param>
        /// <returns>the QueryExpression</returns>
		QueryExpression ParseQueryPrimary(long qp,long xp)
		{
            var lp = lxr.Position;
            var qe = new QueryExpression(lp+1, true);
            switch (tok)
            {
                case Sqlx.LPAREN:
                    Next();
                    qe = ParseQueryExpression(qp,xp);
                    Mustbe(Sqlx.RPAREN);
                    break;
                case Sqlx.SELECT: // query specification
                    {
                        var qs = ParseQuerySpecification(qp,xp);
                        qe = qe + (QueryExpression._Left, qs.defpos);
                        if (qe.domain == Domain.TableType)
                            qe = qe+ (DBObject._Domain, qs.domain)+(DBObject._RowType,qs.rowType);
                        //           if (cx.data.Contains(qs.defpos))
                        //               cx.data += (qe.defpos, cx.data[qs.defpos]);
                        break;
                    }
                case Sqlx.VALUES:
                    var v = BList<SqlValue>.Empty;
                    Sqlx sep = Sqlx.COMMA;
                    while (sep == Sqlx.COMMA)
                    {
                        Next();
                        v += ParseSqlValue(qp,xp);
                        sep = tok;
                    }
                    var sr = new SqlRow(lp, cx, xp, v);
                    qe+=(QueryExpression._Left,cx.Add(sr).defpos);
                    if (qe.domain == Domain.TableType)
                        qe = qe + (DBObject._Domain, sr.domain)+(DBObject._RowType,sr.rowType);
                    break;
                case Sqlx.TABLE:
                    Next();
                    lp = lxr.Position;
                    Ident ic = new Ident(lxr,Sqlx.TABLE);
                    Mustbe(Sqlx.ID);
                    var tb = cx.db.GetObject(ic.ident) as Table ??
                        throw new DBException("42107", ic.ident);
                    cx.obs += (tb.framing,true);
                    var fm = _From(ic, tb, null,new QuerySpecification(lp-1,cx,xp));
                    qe = qe + (QueryExpression._Left, fm.defpos);
                    if (qe.domain == Domain.TableType)
                        qe = qe + (DBObject._Domain, fm.domain) + (DBObject._RowType, fm.rowType);
                    //       if (cx.data.Contains(lp))
                    //           cx.data += (qe.defpos, cx.data[lp]);
                    break;
                default:
                    throw new DBException("42127").Mix();
            }
            var lf = (Query)cx.obs[qe.left];
            qe += (DBObject._RowType, lf.rowType);
            qe += (Query.Display, lf.display);
            qe += (DBObject.Depth, 1 + lf.depth);
            return (QueryExpression)cx.Add(qe);
		}
        /// <summary>
		/// OrderByClause = ORDER BY OrderSpec { ',' OrderSpec } .
        /// </summary>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the list of OrderItems</returns>
		OrderSpec ParseOrderClause(long qp,OrderSpec ord,bool wfok)
		{
			if (tok!=Sqlx.ORDER)
				return ord;
			Next();
			Mustbe(Sqlx.BY);
			ord+=ParseOrderItem(qp,wfok);
			while (tok==Sqlx.COMMA)
			{
				Next();
				ord+=ParseOrderItem(qp,wfok);
			}
            return ord;
		}
        /// <summary>
		/// OrderSpec =  TypedValue [ ASC | DESC ] [ NULLS ( FIRST | LAST )] .
        /// </summary>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>an OrderItem</returns>
		(SqlValue,Domain) ParseOrderItem(long qp,bool wfok)
		{
            var v = ParseSqlValue(qp,Domain.Content.defpos, wfok);
            var dt = v.domain;
            var a = Sqlx.ASC;
            var n = Sqlx.FIRST;
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
            var dm = Domain.Create(cx, dt + (a, n));
			return (v,dm);
		}
        /// <summary>
		/// QuerySpecification = SELECT [ALL|DISTINCT] SelectList [INTO Targets] TableExpression .
        /// </summary>
        /// <param name="t">the expected data type</param>
        /// <returns>The QuerySpecification</returns>
		QuerySpecification ParseQuerySpecification(long qp,long xp)
        {
            Mustbe(Sqlx.SELECT);
            // the list of SelectItems of form * or x.* is managed by cx.stars
            // and the QuerySpec rowType is adjusted inside From constructor
            var os = cx.stars;
            var ol = cx.selectLength;
            cx.stars = BList<(int,long)>.Empty;
            var lp = lxr.Position;
            QuerySpecification r = new QuerySpecification(lp-1,cx,qp)
                + (QuerySpecification.Distinct, ParseDistinctClause());
            cx.Add(r);
            r = ParseSelectList(r,xp); 
            var te = ParseTableExpression(r);
            r = (QuerySpecification)cx.obs[r.defpos];
            r = (QuerySpecification)r.Refresh(cx);
            te = (TableExpression)cx.obs[te.defpos];
            r = r + (QuerySpecification.TableExp, te)
                + (Query.Display, r.rowType.Length);
            r += (DBObject.Depth, Math.Max(Depth(r),1+te.depth));
            r = (QuerySpecification)cx.Add(r);
            if (Match(Sqlx.FOR))
            {
                Next();
                Mustbe(Sqlx.UPDATE);
            }
            //        if (rt!=ObInfo.Any && !rt.CanBeAssigned(r.rowType)) care: R may have aggregations and TE may have grouping
            //            throw new DBException("42000", lxr.pos);
            cx.stars = os;
            cx.selectLength = ol;
            return (QuerySpecification)r.Refresh(cx); // no chances
        }
        int Depth(QuerySpecification q)
        {
            var d = q.tableExp.depth;
            for (var b = q.rowType?.First(); b != null; b = b.Next())
                if (cx.obs[b.value().Item1] is SqlValue v)
                d = Math.Max(d, v.depth);
            return d + 1;
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
        /// <param name="t">the expected data type</param>
		QuerySpecification ParseSelectList(QuerySpecification q,long xp)
        {
            int j = 0;
            SqlValue v;
            var vs = BList<SqlValue>.Empty; 
            (q,v) = ParseSelectItem(q, xp, j++);
            if (v!=null) // star items do not have a value to add at this stage
                vs += v;
            while (tok == Sqlx.COMMA)
            {
                Next();
                (q,v) = ParseSelectItem(q, xp, j++);
                if (v!=null)
                    vs += v;
            }
            return (QuerySpecification)cx.Add(q);
        }
        /// <summary>
		/// SelectItem = * | (Scalar [AS id ]) | (RowValue [.*] [AS IdList]) .
        /// </summary>
        /// <param name="q">the query being parsed</param>
        /// <param name="t">the expected data type for the query</param>
        /// <param name="pos">The position in the SelectList</param>
        (QuerySpecification,SqlValue) ParseSelectItem(QuerySpecification q,long xp,int pos)
        {
            Ident alias;
            cx.selectLength = q.rowType?.Length??0;
            if (tok==Sqlx.TIMES)
            {
                Next();
                cx.stars += (cx.stars.Length,(cx.selectLength,-1L));
                return (q,null);
            }
            var v = ParseSqlValue(q.defpos,xp, true);
            cx.Add(v);
            v = v.AddFrom(cx, q);
            if (v.aggregates(cx))
                q += (Query._Aggregates, true);
            if (tok == Sqlx.AS)
            {
                Next();
                alias = new Ident(lxr,Sqlx.COLUMN);
                var n = v.name;
                var nv = v;
                if (n == "")
                    nv += (Basis.Name, alias.ident);
                else
                    nv += (DBObject._Alias, alias.ident);
                if (cx.defs.Contains(alias.ident))
                {
                    var ob = cx.defs[alias.ident].Item1;
                    if (q.scope.Contains(ob))
                    {
                        var ov = (SqlValue)cx.obs[ob];
                        if (ov.domain.prim == Sqlx.CONTENT && !nv.aggregates(cx))
                            cx.Replace(ov, nv);
                        else
                            cx.Replace(ov, new SqlCopy(ob, cx, ov.name, nv.from, nv));
                    }
                }
                cx.Replace(v, nv);
                cx.defs += (alias, nv);
                cx.Add(nv);
                Mustbe(Sqlx.ID);
                v = nv;
            }
            else
                cx.Add(v);
            if (v.domain.prim == Sqlx.TABLE)
                throw new DBException("42171");
            q += (cx,v);
            q += (Query.Display, q.rowType.Length);
            return ((QuerySpecification)cx.Add(q),v);
        }
        /// <summary>
		/// TableExpression = FromClause [ WhereClause ] [ GroupByClause ] [ HavingClause ] [WindowClause] .
        /// </summary>
        /// <param name="q">the query</param>
        /// <param name="t">the expected data type</param>
        /// <returns>The TableExpression</returns>
		TableExpression ParseTableExpression(QuerySpecification q)
		{
            var lp = lxr.Position;
            var fm = ParseFromClause(q); // query rewriting occurs during these steps
            var wh = ParseWhereClause(q.defpos);
            fm = fm.Refresh(cx);
            var gp = ParseGroupClause(q.defpos);
            var ha = ParseHavingClause(q.defpos);
            var wi = ParseWindowClause(q.defpos);
            fm = fm.Refresh(cx);
            q = (QuerySpecification)cx.obs[q.defpos];
            // we build the tablexpression after the context has settled down
            var nd = BTree<long, bool>.Empty;
            for (var b = q.rowType?.First(); b != null; b = b.Next())
                nd += (b.value().Item1, true);
            var r = new TableExpression(lp, q.Needs(cx,nd), BTree<long, object>.Empty
                + (DBObject._RowType, fm.rowType)
                + (DBObject._Domain, fm.domain)
                + (TableExpression.Target, fm.defpos)
                + (TableExpression.Having, ha)
                + (TableExpression.Windows, wi) // we don't seem to worry about wi.depth
                + (DBObject.Dependents, new BTree<long, bool>(fm.defpos, true)));
            r += (DBObject.Depth, fm.depth+1);
            if (gp != null)
                r += (TableExpression.Group, gp.defpos);   // we don't seem to worry about gp.depth
            r = (TableExpression)cx.Add(r);
            r = (TableExpression)r.AddCondition(cx,Query.Where, wh);
            r = (TableExpression)r.AddCondition(cx,TableExpression.Having, ha);
            fm = (Query)cx.obs[r.target];
            var ds = (fm is QuerySpecification sq) ? sq.display : fm.rowType?.Length??0;
            r+=(Query.Display,ds);
            r = (TableExpression)cx.Add(r);
            q = (QuerySpecification)q.Refresh(cx); // may have changed!
            q += (QuerySpecification.TableExp, r);
            cx.Add(q);
            r = (TableExpression)cx.obs[r.defpos];
            return (TableExpression)r.Conditions(cx);
        }
        /// <summary>
		/// FromClause = 	FROM TableReference { ',' TableReference } .
        /// </summary>
        /// <param name="q">the query being parsed</param>
        /// <returns>The table expression</returns>
		Query ParseFromClause(QuerySpecification q)
		{
            if (tok == Sqlx.FROM)
            {
                Next();
                var rt = ParseTableReference(q);
                q = (QuerySpecification)q.Refresh(cx);
                q += (QuerySpecification.Scope, q.scope + rt.domain.representation);
                cx.Add(q);
                while (tok == Sqlx.COMMA)
                {
                    var lp = lxr.Position;
                    Next();
                    var te = ParseTableReference(q);
                    q = (QuerySpecification)q.Refresh(cx);
                    q += (QuerySpecification.Scope, q.scope + te.domain.representation);
                    cx.Add(q);
                    var jp = new JoinPart(lp)
                    + (JoinPart.JoinKind, Sqlx.CROSS) + (JoinPart.LeftOperand, rt.defpos)
                    + (JoinPart.RightOperand, te.defpos)+(DBObject.Depth,Math.Max(rt.depth,te.depth)+1);
                    rt = jp.Selects(cx,q);
                }
                return (Query)cx.Add(rt);
            }
            else
                return (Query)cx.Add(From._static);
		}
        /// <summary>
		/// TableReference = TableFactor Alias | JoinedTable .
        /// </summary>
        /// <param name="q">the query being parsed</param>
        /// <returns>The table expression</returns>
        Query ParseTableReference(QuerySpecification q)
        {
            var a = ParseTableReferenceItem(q);
            var lp = lxr.Position;
            while (Match(Sqlx.CROSS, Sqlx.NATURAL, Sqlx.JOIN, Sqlx.INNER, Sqlx.LEFT, Sqlx.RIGHT, Sqlx.FULL))
            {
                q = (QuerySpecification)q.Refresh(cx);
                a = ParseJoinPart(lp, a, q);
            }
            return a;
        }
        /// <summary>
		/// TableFactor = 	Table_id [TimePeriodSpecification]
		/// | 	View_id 
		/// | 	Table_FunctionCall 
        /// |   Subquery
        /// |   ROWS '(' int [',' int] ')'
		/// | 	'(' TableReference ')'
		/// | 	TABLE '('  TypedValue ')' 
		/// | 	UNNEST '('  TypedValue ')' 
        /// |   STATIC
        /// |   '[' docs ']' .
        /// Subquery = '(' QueryExpression ')' .
        /// </summary>
        /// <param name="qs">the table expression</param>
        /// <returns>the table expression</returns>
		Query ParseTableReferenceItem(QuerySpecification q)
		{
            Query rf;
            if (tok == Sqlx.ROWS) // Pyrrho specific
            {
                var tr = cx.db as Transaction ?? throw new DBException("2F003");
                Next();
                Mustbe(Sqlx.LPAREN);
                var v = lxr.val;
                Mustbe(Sqlx.INTEGERLITERAL);
                TypedValue w = new TInt(0);
                if (tok == Sqlx.COMMA)
                {
                    Next();
                    w = lxr.val;
                    Mustbe(Sqlx.INTEGERLITERAL);
                }
                Mustbe(Sqlx.RPAREN);
                Ident a = null;
                if (tok == Sqlx.ID || tok == Sqlx.AS)
                {
                    if (tok == Sqlx.AS)
                        Next();
                    a = new Ident(lxr,Sqlx.TABLE);
                    Mustbe(Sqlx.ID);
                }
                if (w.ToInt() != 0)
                    rf = (Query)cx.Add(new LogRowColTable(tr, cx, (long)v.Val(), (long)w.Val(), 
                        a?.ident??""));
                else
                    rf = (Query)cx.Add(new LogRowTable(tr, cx, (long)v.Val(), a?.ident ?? ""));
            }
            else if (tok == Sqlx.UNNEST)
            {
                Next();
                var lp = lxr.Position;
                Mustbe(Sqlx.LPAREN);
                SqlValue sv;
                if (tok == Sqlx.LBRACK) // allow [ ] for doc array
                {
                    var da = new SqlRowArray(lxr.Position, BTree<long, object>.Empty);
                    Next();
                    if (tok != Sqlx.RBRACK)
                        while (true)
                        {
                            da += ParseSqlRow(q.defpos,Domain.Document.defpos);
                            if (tok != Sqlx.COMMA)
                                break;
                            Next();
                        }
                    Mustbe(Sqlx.RBRACK);
                    sv = da;
                }
                else
                    sv = ParseSqlValue(q.defpos,Domain.TableType.defpos); //  unnest forces table value
                rf = From._static + (From.Target, sv.defpos);
                Mustbe(Sqlx.RPAREN);
            }
            else if (tok == Sqlx.TABLE)
            {
                Next();
                Mustbe(Sqlx.LPAREN); // SQL2003-2 7.6 required before table valued function
                Ident n = new Ident(lxr,Sqlx.TABLE);
                Mustbe(Sqlx.ID);
                var r = RowType.Empty;
                Mustbe(Sqlx.LPAREN);
                if (tok != Sqlx.RPAREN)
                    for (; ; )
                    {
                        var x = ParseSqlValue(q.defpos, Domain.Content.defpos);
                        x = (SqlValue)cx.Add(x);
                        r += (x.defpos,x.domain);
                        if (tok == Sqlx.RPAREN)
                            break;
                        Mustbe(Sqlx.COMMA);
                    }
                Next();
                Mustbe(Sqlx.RPAREN); // another: see above
                var proc = cx.db.GetProcedure(n.ident, (int)r.Count)
                    ?? throw new DBException("42108", n.ident + "$" + r.Count).Mix();
                cx.obs += (proc.framing,true);
                var cr = ParseCorrelation(proc.defpos);
                var cs = new CallStatement(n.iix, proc, proc.name, r);
                rf = new From(n, cx, cs, q);
                if (cx.db.parse == ExecuteStatus.Obey)
                {
                    proc.Exec(cx, r);
                    cx.data+=(n.iix,(RowSet)cx.val);
                }
            }
            else if (tok == Sqlx.LPAREN) // subquery
            {
                var lp = lxr.Position;
                Next();
                QueryExpression qe = ParseQueryExpression(q.defpos,q.defpos)+(DBObject._From,q.defpos);
                Mustbe(Sqlx.RPAREN);
                var r = ParseCorrelation(qe.defpos);
                var cs = new CursorSpecification(lp);
                if (r == null)
                    cs = cs + (DBObject._RowType, qe.rowType) + (DBObject._Domain, qe.domain);
                else
                    cs = cs + (Basis.Name, r.name)
                    + (DBObject._RowType, r.rowType) + (DBObject._Domain, r.domain);
                rf = cs._Union(qe);
            }
            else if (tok == Sqlx.STATIC)
            {
                Next();
                rf = From._static;
            }
            else if (tok == Sqlx.LBRACK)
            {
                var lp = lxr.Position;
                rf = From._static + (From.Target, ParseSqlDocArray().defpos);
            }
            else // ordinary table, view, OLD/NEW TABLE id, or parameter
            {
                Ident ic = new Ident(lxr,Sqlx.TABLE);
                Mustbe(Sqlx.ID);
                string a = null;
                if (tok == Sqlx.ID)
                {
                    a = lxr.val.ToString();
                    Next();
                }
                Table tb = (cx.db.GetObject(ic.ident)??cx.obs[cx.defs[ic].Item1])  as Table
                    ?? throw new DBException("42107",ic.ident); // View TBD
                rf = _From(ic, tb, a, q); 
                q = (QuerySpecification)cx.obs[q.defpos];
                if (Match(Sqlx.FOR))
                {
                    var ps = ParsePeriodSpec(q.defpos);
                    rf += (Query.Periods, rf.periods + (tb.defpos, ps));
                    long pp = (ps.periodname == "SYSTEM_TIME") ? tb.systemPS : tb.applicationPS;
                    if (pp<0)
                        throw new DBException("42162", ps.periodname).Mix();
                    rf += (Query.Periods, rf.periods + (tb.defpos, ps));
                }
                if (cx.dbformat < 51)
                    cx.defs += (new Ident(rf.defpos.ToString(), rf.defpos, Sqlx.TABLE), rf);
                if (a != null)
                {
                    var ia = new Ident(a, rf.defpos, Sqlx.TABLE);
                    cx.defs += (ia, rf);
                    tb.AddCols(cx, ia, rf.rowType);
                }
            }
            rf = (Query)cx.Add(rf);
            for (var b = q.rowType?.First(); b != null; b = b.Next())
            {
                var c = cx.obs[b.value().Item1] as SqlValue;
                if (c == null || c.domain.prim!=Sqlx.CONTENT)
                    continue;
                SqlValue nc;
                (nc, rf) = c.Resolve(cx, rf);
                if (nc != c)
                    cx.Replace(c, nc);
            }
            cx.Add(rf);
            return rf.Refresh(cx); // but check anyway
        }
        /// <summary>
        /// The From constructor will sort out the rowType, 
        /// based on the needs of the SelectList.
        /// Then we will resolve undefined expressions in the SelectList.
        /// </summary>
        /// <param name="dp">The occurrence of this table</param>
        /// <param name="tb">The table</param>
        /// <param name="a">The alias or null</param>
        /// <param name="q">The query with the select list</param>
        /// <returns></returns>
        From _From(Ident ic,Table tb,string a,QuerySpecification q)
        {
            var dp = ic.iix;
            DBObject ut = null, ua = null;
            if (tb!=null)
            {
                if (cx.db.role.infos[tb.defpos] is ObInfo oi && cx.defs.Contains(oi.name))
                    ut = cx.obs[cx.defs[oi.name].Item1];
                if (a != null && cx.defs.Contains(a))
                    ua = cx.obs[cx.defs[a].Item1];
            }
            var fm = new From(ic, cx, tb, q, Grant.Privilege.Select, a);
            if (ut!=null && ut.mem[DBObject._Domain] == Domain.Content)
                cx.Replace(ut, fm);
            if (ua != null)// && ua.mem[DBObject._Domain]==Domain.Content)
                cx.Replace(ua, fm);
            fm = (From)cx.Add(fm);
            tb.AddCols(cx, ic, fm.rowType);
            var cm = BTree<long, long>.Empty;
            var qn = q?.Needs(cx, BTree<long,bool>.Empty);
            // Resolution stage
            for (var b = qn?.First(); b != null; b = b.Next())
            {
                var c = b.key();
                var co = cx.obs[c] as SqlValue;
                var (nc, nf) = co?.Resolve(cx, fm, a) ?? (co, fm);
                if (co != nc)
                {
                    if (nc is SqlCopy sc)
                        cm += (sc.copyFrom, nc.defpos);
                }
                if (nf != null)
                    fm = (From)nf;
                if (co != nc)
                    cx.Replace(co, nc);
            }
            fm = (From)cx.Add(fm);
            if (a != "" && a != null)
            {
                fm += (DBObject._Alias, a);
                cx.defs += (new Ident(a, dp, Sqlx.TABLE), fm);
                fm += (DBObject._Alias, a);
                fm = (From)cx.Add(fm);
            }
            return fm;
        }
        /// <summary>
        /// TimePeriodSpec = 
        ///    |    AS OF TypedValue
        ///    |    BETWEEN(ASYMMETRIC|SYMMETRIC)  TypedValue AND TypedValue
        ///    |    FROM TypedValue TO TypedValue .
        /// </summary>
        /// <returns>The periodSpec</returns>
        PeriodSpec ParsePeriodSpec(long qp)
        {
            var r = new PeriodSpec();
            Next();
            if (tok == Sqlx.ID)
                r.periodname = lxr.val.ToString();
            Mustbe(Sqlx.SYSTEM_TIME,Sqlx.ID);
            r.op = tok;
            var xp = Domain.UnionDate.defpos;
            switch (tok)
            {
                case Sqlx.AS: Next();
                    Mustbe(Sqlx.OF);
                    r.time1 = ParseSqlValue(qp,Domain.UnionDate.defpos);
                    break;
                case Sqlx.BETWEEN: Next();
                    r.op = Sqlx.ASYMMETRIC;
                    if (Match(Sqlx.ASYMMETRIC))
                        Next();
                    else if (Match(Sqlx.SYMMETRIC))
                    {
                        Next();
                        r.op = Sqlx.SYMMETRIC;
                    }
                    r.time1 = ParseSqlValueTerm(qp,Domain.UnionDate.defpos, false);
                    Mustbe(Sqlx.AND);
                    r.time2 = ParseSqlValue(qp,Domain.UnionDate.defpos);
                    break;
                case Sqlx.FROM: Next();
                    r.time1 = ParseSqlValue(qp,Domain.UnionDate.defpos);
                    Mustbe(Sqlx.TO);
                    r.time2 = ParseSqlValue(qp,Domain.UnionDate.defpos);
                    break;
                default:
                    r.op=Sqlx.NO;
                    break;
            }
            return r;
        }
        /// <summary>
        /// Cols = [ ident {, ident }]
        /// Pick a subset of current identifiers
        /// </summary>
        /// <param name="xp"></param>
        /// <returns></returns>
        BList<Ident> ParseCols()
        {
            if (tok==Sqlx.ID)
            { 
                var ids = ParseIDList(Sqlx.COLUMN);
                Mustbe(Sqlx.RPAREN);
                return ids;
            }
            return null;
        }
        /// <summary>
        /// Alias = 		[[AS] id [ Cols ]] .
        /// Creates a new ObInfo for the derived table.
        /// </summary>
        /// <returns>The correlation info</returns>
		ObInfo ParseCorrelation(long xp)
		{
            var oi = cx.db.role.infos[xp] as ObInfo;
            if (tok == Sqlx.ID || tok == Sqlx.AS)
			{
				if (tok==Sqlx.AS)
					Next();
                var lp = lxr.Position;
                var cs = RowType.Empty;
                var rs = BTree<long, Domain>.Empty;
                var dm = oi.domain;
                var tablealias = new Ident(lxr,Sqlx.TABLE);
				Mustbe(Sqlx.ID);
				if (tok==Sqlx.LPAREN)
				{
					Next();
                    var ids = ParseIDList(Sqlx.COLUMN);
                    if (ids.Length != oi.Length)
                        throw new DBException("22000");
                    var ib = ids.First();
                    for (var b = oi.rowType?.First(); ib!=null && b != null; b = b.Next(), ib=ib.Next())
                    {
                        var oc = b.value().Item1;
                        var ic = ib.value();
                        var cp = ic.iix;
                        var cd = oi.domain.representation[oc];
                        cs += (cp, cd);
                        rs += (cp, cd);
                    }
                    dm = new Domain(lp, Domain.TableType, rs);
                    return new ObInfo(lp, tablealias.ident, Sqlx.TABLE, dm, cs);
				} else
                    return new ObInfo(lp, tablealias.ident, Sqlx.TABLE, dm);
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
        /// <param name="r">The Query so far</param>
        /// <returns>the updated query</returns>
        Query ParseJoinPart(long dp,Query fi,QuerySpecification q)
        {
            var left = fi;
            Sqlx jkind;
            Query right;
            JoinPart v = new JoinPart(dp);
            if (Match(Sqlx.CROSS))
            {
                jkind = tok;
                Next();
                Mustbe(Sqlx.JOIN);
                right = ParseTableReferenceItem(q);
            }
            else if (Match(Sqlx.NATURAL))
            {
                v += (JoinPart.Natural, tok);
                Next();
                jkind = ParseJoinType();
                Mustbe(Sqlx.JOIN);
                right = ParseTableReferenceItem(q);
            }
            else
            {
                jkind = ParseJoinType();
                Mustbe(Sqlx.JOIN);
                right = ParseTableReferenceItem(q);
                if (tok == Sqlx.USING)
                {
                    v += (JoinPart.Natural, tok);
                    Next();
                    var cs = BList<long>.Empty;
                    var ids = ParseIDList(Sqlx.COLUMN);
                    for (var i = 0; i < ids.Length; i++)
                        cs += ids[i].iix;
                    v += (JoinPart.NamedCols, cs);
                }
                else
                {
                    Mustbe(Sqlx.ON);
                    var oc = ParseSqlValue(fi.defpos,Domain.Bool.defpos).Disjoin(cx);
                    left = (Query)cx.obs[left.defpos];
                    right = (Query)cx.obs[right.defpos];
                    var ls = BList<SqlValue>.Empty;
                    var rs = BList<SqlValue>.Empty;
                    var lm = cx.Map(left.rowType);
                    var rm = cx.Map(right.rowType);
                    for (var b = oc.First(); b != null; b = b.Next())
                    {
                        var se = cx.obs[b.key()] as SqlValueExpr;
                        if (se == null || se.op != Sqlx.EQL) throw new DBException("42151");
                        var lf = se.left;
                        var rg = se.right;
                        if (lf == -1L || rg == -1L) throw new DBException("42151");
                        var rev = !lm.Contains(lf);
                        if (rev)
                        {
                            if ((!rm.Contains(lf))
                                || (!lm.Contains(rg)))
                                throw new DBException("42151");
                            oc += (cx.Add(new SqlValueExpr(se.defpos, cx, Sqlx.EQL, 
                                (SqlValue)cx.obs[rg], (SqlValue)cx.obs[lf], Sqlx.NO)).defpos,true);
                            ls += (SqlValue)cx.obs[rg];
                            rs += cx.obs[lf] as SqlValue;
                        }
                        else
                        {
                            if (!rm.Contains(rg))
                                throw new DBException("42151");
                            ls += cx.obs[lf] as SqlValue;
                            rs += (SqlValue)cx.obs[rg];
                        }
                    }
                    v += (JoinPart.JoinCond,oc);
                    left = (Query)cx.obs[left.defpos] 
                        + (Query.OrdSpec, new OrderSpec(ls));
                    right = (Query)cx.obs[right.defpos] 
                        + (Query.OrdSpec, new OrderSpec(rs));
                }
            }
            v += (JoinPart.JoinKind, jkind);
            v += (JoinPart.LeftOperand, left.defpos);
            v += (JoinPart.RightOperand, right.defpos);
            //       for (var b = v.joinCond.First(); b != null; b = b.Next())
            //         r.Needs(b.value().alias ?? b.value().name, Query.Need.joined);
            q = (QuerySpecification)q.Refresh(cx);
            var r = (JoinPart)v.Selects(cx, q);
            q = (QuerySpecification)cx.obs[q.defpos];
            r = (JoinPart)r.Conditions(cx);
            r = (JoinPart)r.Orders(cx,q.ordSpec);
            return (JoinPart)cx.Add(r);
        }
        /// <summary>
		/// GroupByClause = GROUP BY [DISTINCT|ALL] GroupingElement { ',' GroupingElement } .
        /// GroupingElement = GroupingSet | (ROLLUP|CUBE) '('GroupingSet {',' GroupingSet} ')'  
        ///     | GroupSetsSpec | '('')' .
        /// GroupingSet = Col | '(' Col {',' Col } ')' .
        /// GroupingSetsSpec = GROUPING SETS '(' GroupingElement { ',' GroupingElement } ')' .
        /// </summary>
        /// <returns>The GroupSpecification</returns>
        GroupSpecification ParseGroupClause(long qp)
        {
            if (tok != Sqlx.GROUP)
                return null;
            Next();
            var lp = lxr.Position;
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
            GroupSpecification r = new GroupSpecification(lp,BTree<long, object>.Empty
                + (GroupSpecification.DistinctGp, d));
            r = ParseGroupingElement(r,ref simple);
            while (tok == Sqlx.COMMA)
            {
                Next();
                r = ParseGroupingElement(r,ref simple);
            }
            lp = lxr.Position;
            // simplify: see SQL2003-02 7.9 SR 10 .
            if (simple && r.sets.Count > 1)
            {
                Grouping gn = new Grouping(lp);
                var i = 0;
                for (var g = r.sets.First();g!=null;g=g.Next())
                    for (var h = ((Grouping)cx.obs[g.value()]).members.First(); h!=null;h=h.Next())
                        gn += (Grouping.Members,gn.members+(h.key(),i++));
                cx.Add(gn);
                r += (GroupSpecification.Sets, new BList<long>(gn.defpos));
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
                var cn = ParseIdent(Sqlx.COLUMN);
                var ls = new Grouping(cn.iix, BTree<long, object>.Empty + (Grouping.Members,
                    new BTree<long, int>(cx.Get(cn, Domain.Content.defpos).defpos, 0)));
                cx.Add(ls);
                g += (GroupSpecification.Sets,g.sets+ls.defpos);
                simple = true;
                return (GroupSpecification)cx.Add(g);
            }
            simple = false;
            if (Match(Sqlx.LPAREN))
            {
                var lp = lxr.Position;
                Next();
                if (tok == Sqlx.RPAREN)
                {
                    Next();
                    g += (GroupSpecification.Sets,g.sets+cx.Add(new Grouping(lp)).defpos);
                    return (GroupSpecification)cx.Add(g);
                }
                g +=(GroupSpecification.Sets,g.sets+cx.Add(ParseGroupingSet()).defpos);
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
            var cn = ParseIdent(Sqlx.COLUMN);
            var t = new Grouping(cn.iix,BTree<long,object>.Empty
                +(Grouping.Members,new BTree<long,int>(cn.iix,0)));
            var i = 1;
            while (Match(Sqlx.COMMA))
            {
                cn = ParseIdent(Sqlx.COLUMN);
                t+=(Grouping.Members,t.members+(cn.iix,i++));
            }
            Mustbe(Sqlx.RPAREN);
            return (Grouping)cx.Add(t);
        }
        /// <summary>
		/// HavingClause = HAVING BooleanExpr .
        /// </summary>
        /// <returns>The SqlValue (Boolean expression)</returns>
		BTree<long,bool> ParseHavingClause(long qp)
        {
            var r = BTree<long,bool>.Empty;
            if (tok != Sqlx.HAVING)
                return r;
            Next();
            var lp = lxr.Position;
            var d = ParseSqlValueDisjunct(qp,Domain.Bool.defpos, false);
            if (tok != Sqlx.OR)
            {
                foreach (var s in d)
                {
                    r +=(s.defpos, true);
       //             lxr.context.cur.Needs(s.alias ?? s.name, Query.Need.condition);
                }
                return r;
            }
            var left = Disjoin(d);
            while (tok == Sqlx.OR)
            {
                Next();
                left = new SqlValueExpr(lp, cx, Sqlx.OR, left, 
                    Disjoin(ParseSqlValueDisjunct(qp,Domain.Bool.defpos, false)), Sqlx.NO);
            }
            r +=(left.defpos, true);
      //      lxr.context.cur.Needs(left.alias ?? left.name, Query.Need.condition);
            return r;
        }
        /// <summary>
		/// WhereClause = WHERE BooleanExpr .
        /// </summary>
        /// <returns>The SqlValue (Boolean expression)</returns>
		BTree<long,bool> ParseWhereClause(long qp)
		{
            var r = BTree<long,bool>.Empty;
            if (tok != Sqlx.WHERE)
                return r;
			Next();
            var d = ParseSqlValueDisjunct(qp,Domain.Bool.defpos, false);
            if (tok != Sqlx.OR)
            {
                foreach (var s in d)
                    r +=(s.defpos, true);
                //              lxr.context.cur.Needs(s.alias ?? s.name, Query.Need.condition);
                return r;
            }
            var left = Disjoin(d);
            while (tok == Sqlx.OR)
            {
                var lp = lxr.Position;
                Next();
                left = new SqlValueExpr(lp, cx, Sqlx.OR, left, 
                    Disjoin(ParseSqlValueDisjunct(qp,Domain.Bool.defpos, false)), Sqlx.NO);
                left = (SqlValue)cx.Add(left);
            }
            r +=(left.defpos, true);
     //       lxr.context.cur.Needs(left.alias ?? left.name,Query.Need.condition);
            return r;
		}
        /// <summary>
		/// WindowClause = WINDOW WindowDef { ',' WindowDef } .
        /// </summary>
        /// <returns>the window set as a tree by window names</returns>
        BTree<string,WindowSpecification> ParseWindowClause(long qp)
        {
            if (tok != Sqlx.WINDOW)
                return null;
            Next();
            var tree = BTree<string,WindowSpecification>.Empty; // of WindowSpecification
            tree = ParseWindowDefinition(qp,tree);
            while (tok == Sqlx.COMMA)
            {
                Next();
                tree = ParseWindowDefinition(qp,tree);
            }
            return tree;
        }
        /// <summary>
		/// WindowDef = id AS '(' WindowDetails ')' .
        /// </summary>
        /// <param name="tree">ref: the tree of windowdefs</param>
        BTree<string,WindowSpecification> ParseWindowDefinition(long qp,BTree<string, WindowSpecification> tree)
        {
            var id = lxr.val;
            Mustbe(Sqlx.ID);
            Mustbe(Sqlx.AS);
            WindowSpecification r = ParseWindowSpecificationDetails(qp);
            if (r.orderWindow != null)
            {
                WindowSpecification ow = tree[r.orderWindow];
                if (ow==null)
                    throw new DBException("42135", r.orderWindow).Mix();
                if (r.partition != 0)
                    throw new DBException("42000", "7.11 SR10c").ISO();
                if (ow.order != null && r.order != null)
                    throw new DBException("42000", "7.11 SR10d").ISO();
                if (ow.units != Sqlx.NO || ow.low != null || ow.high != null)
                    throw new DBException("42000", "7.11 SR10e").ISO();
            }
            return tree+ (id.ToString(), r);
        }
        /// <summary>
        /// An SQL insert statement
        /// </summary>
        /// <param name="cx">the context</param>
        /// <param name="sql">the sql</param>
        /// <returns>the SqlInsert</returns>
        internal Context ParseSqlInsert(Context cx,string sql)
        {
            lxr = new Lexer(sql);
            tok = lxr.tok;
            if (lxr.Position > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
            return ParseSqlInsert(-1L).Item1;
        }
        /// <summary>
		/// Insert = INSERT [WITH (PROVENANCE|TYPE_URI) string][XMLOption] INTO Table_id [ Cols ]  TypedValue [Classification].
        /// </summary>
        /// <returns>the executable</returns>
        (Context,SqlInsert) ParseSqlInsert(long qp)
        {
            string prov = null;
            bool with = false;
            var lp = lxr.Position;
            Next();
            if (tok == Sqlx.WITH)
            {
                Next();
                if (Match(Sqlx.PROVENANCE, Sqlx.TYPE_URI))
                {
                    Next();
                    prov = lxr.val.ToString();
                    Mustbe(Sqlx.CHARLITERAL);
                }
                else
                    with = true;
            }
            ParseXmlOption(with);
            Mustbe(Sqlx.INTO);
            Ident ic = new Ident(lxr,Sqlx.TABLE);
            Mustbe(Sqlx.ID);
            var tb = cx.db.GetObject(ic.ident) as Table ??
                throw new DBException("42107", ic.ident);
            cx.obs += (tb.framing,true);
            var ti = cx._RowType(tb.defpos);
            cx.defs += (ic, tb);
            tb.AddCols(cx,ic, ti, true);
            BList<Ident> cs = null;
            // Ambiguous syntax here: (Cols) or (Subquery) or other possibilities
            if (tok == Sqlx.LPAREN)
            {
                Next();
                cs = ParseCols();
                if (cs == null)
                    tok = lxr.PushBack(Sqlx.LPAREN);
            }
            var fm = new From(ic, cx, tb, null, Grant.Privilege.Insert, null, cs);
            cx.Add(fm);
            cx.defs += (ic, fm);
            tb.AddCols(cx,ic, fm.rowType);
            SqlValue v;
            var vp = lxr.Position;
            if (tok == Sqlx.DEFAULT)
            {
                Next();
                Mustbe(Sqlx.VALUES);
                v = SqlNull.Value;
            }
            else
                // care: we might have e.g. a subquery here
                v = ParseSqlValue(qp,fm.defpos);
            if (v is SqlRow) // tolerate a single value without the VALUES keyword
                v = new SqlRowArray(vp, cx, v.defpos, new BList<long>(v.defpos));
            SqlInsert s = new SqlInsert(lp,fm, prov, v);
            if (Match(Sqlx.SECURITY))
            {
                Next();
                if (cx.db.user.defpos != cx.db.owner)
                    throw new DBException("42105");
                s += (DBObject.Classification, MustBeLevel());
            }
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                cx = tr.Execute(s, cx);
            return (cx,(SqlInsert)cx.Add(s));
        }
        /// <summary>
		/// DeleteSearched = DELETE [XMLOption] FROM Table_id [ WhereClause] .
        /// </summary>
        /// <returns>the executable</returns>
		Executable ParseSqlDelete()
        {
            var lp = lxr.Position;
            Next();
            ParseXmlOption(false);
            Mustbe(Sqlx.FROM);
            Ident a = null;
            Ident ic = new Ident(lxr,Sqlx.TABLE);
            Mustbe(Sqlx.ID);
            if (tok == Sqlx.AS)
                Next();
            if (tok == Sqlx.ID)
            {
                a = new Ident(lxr,Sqlx.TABLE);
                Next();
            }
            var tb = cx.db.GetObject(ic.ident) as Table ?? // TBD for views
                throw new DBException("42107", ic.ident);
            QuerySearch r = new QuerySearch(lp, cx, ic, tb,  Grant.Privilege.Delete);
            cx.defs += (ic, r);
            var u = cx.GetUid();
            cx.Add(r);
            var fm = (From)cx.obs[r.table];
            for (var b = fm.rowType?.First(); b!=null; b=b.Next())
            {
                var p = b.value().Item1;
                var co = (SqlValue)cx.obs[p];
                cx.defs += (new Ident(co.name, p, Sqlx.COLUMN), co);
            }
            r = (QuerySearch)cx.Add(r + (SqlInsert._Table,fm.defpos));
            var wh = ParseWhereClause(r.defpos);
            fm = (From)cx.obs[fm.defpos];
            cx.Replace(fm,fm.AddCondition(cx, Query.Where, wh));
            r = (QuerySearch)cx.obs[r.defpos];
            fm = (From)cx.obs[fm.defpos];
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                cx = fm.Delete(cx, BTree<string, bool>.Empty, new Adapters());
            return (Executable)cx.Add(r);
        }
        /// <summary>
        /// the update statement
        /// </summary>
        /// <param name="cx">the context</param>
        /// <param name="sql">the sql</param>
        /// <returns>the updatesearch</returns>
        internal Context ParseSqlUpdate(Context cx, string sql)
        {
            lxr = new Lexer(sql);
            tok = lxr.tok;
            if (lxr.Position > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length);
            return ParseSqlUpdate().Item1;
        }
        /// <summary>
		/// UpdateSearched = UPDATE [XMLOption] Table_id Assignment [WhereClause] .
        /// </summary>
        /// <returns>The UpdateSearch</returns>
		(Context,Executable) ParseSqlUpdate()
        {
            var lp = lxr.Position; 
            Next();
            ParseXmlOption(false);
            Ident ic = new Ident(lxr,Sqlx.TABLE);
            Ident a = null;
            Mustbe(Sqlx.ID);
            if (tok != Sqlx.SET)
            {
                if (tok == Sqlx.AS)
                    Next();
                a = new Ident(lxr,Sqlx.TABLE);
                Mustbe(Sqlx.ID);
            }
            Mustbe(Sqlx.SET);
            var tb = cx.db.GetObject(ic.ident) as Table ?? // TBD for views
                throw new DBException("42107", ic.ident);
            UpdateSearch r = new UpdateSearch(lp, cx, ic, tb, Grant.Privilege.Update);
            var fm = (From)cx.obs[r.table]; 
            cx.defs += (ic, fm);
            var ua = ParseAssignments(fm.defpos);
            fm = (From)cx.obs[fm.defpos];
            fm += (From.Assigns, ua);
            cx.Add(fm);
             var wh = ParseWhereClause(r.defpos);
            fm = (From)cx.obs[fm.defpos];
            fm = (From)fm.AddCondition(cx,Query.Where, wh);
            r += (SqlInsert._Table, fm.defpos);
            cx.Add(r);
            fm = (From)cx.obs[r.table];
            if (cx.db.parse == ExecuteStatus.Obey && cx.db is Transaction tr)
                cx = fm.Update(cx, BTree<string, bool>.Empty, new Adapters(), new List<RowSet>());
            r = (UpdateSearch)cx.Add(r);
            return (cx,r);
        }
        internal BList<UpdateAssignment> ParseAssignments(string sql,long xp)
        {
            lxr = new Lexer(sql);
            tok = lxr.tok;
            return ParseAssignments(xp);
        }
        /// <summary>
        /// Assignment = 	SET Target '='  TypedValue { ',' Target '='  TypedValue }
        /// </summary>
        /// <returns>the list of assignments</returns>
		BList<UpdateAssignment> ParseAssignments(long xp)
		{
            var r = BList<UpdateAssignment>.Empty + ParseUpdateAssignment(xp);
            while (tok==Sqlx.COMMA)
			{
				Next();
				r+=ParseUpdateAssignment(xp);
			}
			return r;
		}
        /// <summary>
        /// Target '='  TypedValue
        /// </summary>
        /// <returns>An updateAssignmentStatement</returns>
		UpdateAssignment ParseUpdateAssignment(long xp)
        {
            SqlValue vbl;
            SqlValue val;
            Match(Sqlx.SECURITY);
 /*           if (tok == Sqlx.SECURITY)
            {
                if (tr.user.defpos != tr.owner)
                    throw new DBException("42105");
                sa.vbl = new LevelTarget(lxr);
                Next();
            }
            else */
            {
                var ic = new Ident(lxr,Sqlx.VALUE);
                Mustbe(Sqlx.ID);
                vbl = (SqlValue)cx.Get(ic,xp)
                    ?? throw new DBException("42112", ic.ToString()).Mix();
                vbl = Reify(vbl, ic);
            }
            Mustbe(Sqlx.EQL);
            val = ParseSqlValue(xp,vbl.defpos);
            return new UpdateAssignment(vbl.defpos,val.defpos);
        }
        /// <summary>
        /// Parse an SQL Value
        /// </summary>
        /// <param name="s">The string to parse</param>
        /// <param name="t">the expected data type if any</param>
        /// <returns>the SqlValue</returns>
        internal SqlValue ParseSqlValue(string s,long xp)
        {
            lxr = new Lexer(s);
            tok = lxr.tok;
            return ParseSqlValue(-1,xp);
        }
        internal SqlValue ParseSqlValue(Ident ic, long xp)
        {
            lxr = new Lexer(ic.ident,ic.iix);
            tok = lxr.tok;
            return ParseSqlValue(-1,xp);
        }
        /// <summary>
        /// Alas the following informal syntax is not a good guide to the way LL(1) has to go...
		///  Value = 		Literal
        /// |   ID ':'  TypedValue
		/// |	Value BinaryOp TypedValue 
		/// | 	'-'  TypedValue 
		/// |	'('  TypedValue ')'
		/// |	Value Collate 
		/// | 	Value '['  TypedValue ']'
		/// |	ColumnRef  
		/// | 	VariableRef
        /// |   PeriodName
        /// |   PERIOD '('  TypedValue,  TypedValue ')'
		/// |	VALUE 
        /// |   ROW
		/// |	Value '.' Member_id
		/// |	MethodCall
		/// |	NEW MethodCall 
		/// | 	FunctionCall 
		/// |	VALUES  '('  TypedValue { ','  TypedValue } ')' { ',' '('  TypedValue { ','  TypedValue } ')' }
		/// |	Subquery
		/// |	( MULTISET | ARRAY | ROW) '('  TypedValue { ','  TypedValue } ')'
		/// | 	TABLE '('  TypedValue ')' 
		/// |	TREAT '('  TypedValue AS Sub_Type ')'  .
        /// PeriodName = SYSTEM_TIME | id .
        /// </summary>
        /// <param name="t">a constraint on usage</param>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the SqlValue</returns>
        internal SqlValue ParseSqlValue(long qp, long xp,bool wfok=false)
        {
            if (tok == Sqlx.NULL)
            {
                Next();
                return SqlNull.Value;
            }
            if (tok == Sqlx.PERIOD)
            {
                Next();
                Mustbe(Sqlx.LPAREN);
                var op1 = ParseSqlValue(qp,Domain.UnionDate.defpos);
                Mustbe(Sqlx.COMMA);
                var op2 = ParseSqlValue(qp,Domain.UnionDate.defpos);
                Mustbe(Sqlx.RPAREN);
                var r = new SqlValueExpr(lxr.Position, cx, Sqlx.PERIOD, op1, op2, Sqlx.NO);
                return (SqlValue)cx.Add(r);
            }
            SqlValue left;
            left = Disjoin(ParseSqlValueDisjunct(qp,xp,wfok));
            while (left.domain.prim==Sqlx.BOOLEAN && tok==Sqlx.OR)
            {
                Next();
                left = new SqlValueExpr(lxr.Position, cx, Sqlx.OR, left, 
                    Disjoin(ParseSqlValueDisjunct(qp,xp,wfok)), Sqlx.NO);
            }
            while (Match(Sqlx.UNION, Sqlx.EXCEPT, Sqlx.INTERSECT))
            {
                var lp = lxr.Position;
                var op = tok;
                var m = Sqlx.NO;
                Next();
                if ((op == Sqlx.UNION || op == Sqlx.EXCEPT) && Match(Sqlx.ALL, Sqlx.DISTINCT))
                {
                    m = tok;
                    Next();
                }
                var right = ParseSqlValueTerm(qp,xp,wfok);
                left = new SqlValueExpr(lp, cx, op, left, right, m);
            }
            return ((SqlValue)cx.Add(left));
        }
        SqlValue Disjoin(List<SqlValue> s)
        {
            var right = s[s.Count - 1];
            for (var i = s.Count - 2; i >= 0; i--)
                right = new SqlValueExpr(lxr.Position, cx, Sqlx.AND, s[i], right, Sqlx.NO);
            return (SqlValue)cx.Add(right);
        }
        List<SqlValue> ParseSqlValueDisjunct(long qp,long xp,bool wfok)
        {
            var r = new List<SqlValue>();
            var left = ParseSqlValueConjunct(qp,xp,wfok);
            r.Add(left);
            while (left.domain.prim==Sqlx.BOOLEAN && Match(Sqlx.AND))
            {
                Next();
                left = ParseSqlValueConjunct(qp,xp,wfok);
                r.Add(left);
            }
            return r;
        }
        SqlValue ParseSqlValueConjunct(long qp,long xp,bool wfok)
        {
            var left = ParseSqlValueExpression(qp,xp,wfok);
            if (Match(Sqlx.EQL, Sqlx.NEQ, Sqlx.LSS, Sqlx.GTR, Sqlx.LEQ, Sqlx.GEQ))
            {
                var op = tok;
                var lp = lxr.Position;
                Next();
                return (SqlValue)cx.Add(new SqlValueExpr(lp, cx,
                    op, left, ParseSqlValueExpression(qp,left.defpos,wfok), Sqlx.NO));
            }
            return (SqlValue)cx.Add(left);
        }
        SqlValue ParseSqlValueExpression(long qp,long xp,bool wfok)
        {
            var left = ParseSqlValueTerm(qp,xp,wfok);
            while (Domain.UnionDateNumeric.CanTakeValueOf(cx,left.domain) 
                && Match(Sqlx.PLUS, Sqlx.MINUS))
            {
                var op = tok;
                var lp = lxr.Position;
                Next();
                var x = ParseSqlValueTerm(qp,xp, wfok);
                left = (SqlValue)cx.Add(new SqlValueExpr(lp, cx, op, left, x, Sqlx.NO));
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
        /// <param name="t">the expected data type</param>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the sqlValue</returns>
        SqlValue ParseSqlValueTerm(long qp,long xp,bool wfok)
        {
            bool sign = false, not = false;
            var lp = lxr.Position;
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
            var left = ParseSqlValueFactor(qp,xp,wfok);
            if (sign)
                left = new SqlValueExpr(lp, cx, Sqlx.MINUS, null, left, Sqlx.NO);
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
                lp = lxr.Position;
                Next();
                return (SqlValue)cx.Add(new SqlValueExpr(lp, cx,
                    op, left, ParseSqlValueFactor(qp,left.defpos,wfok), imm));
            }
//            if (Match(Sqlx.AND))
//            {
//                while (Match(Sqlx.AND))
//                {
//                    lp = lxr.Position;
//                    Next();
//                    left = new SqlValueExpr(lp, Sqlx.AND, left, ParseSqlValueTerm(wfok),Sqlx.NO);
//                }
//                return (SqlValue)cx.Add(left);
//            }
            while (Match(Sqlx.TIMES, Sqlx.DIVIDE,Sqlx.MULTISET))
            {
                Sqlx op = tok;
                lp = lxr.Position;
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
                left = new SqlValueExpr(lp, cx, op, left, 
                    ParseSqlValueFactor(qp,left.defpos,wfok), m);
            }
            if (!cx.Dom(xp).CanTakeValueOf(cx, left.domain))
                throw new DBException("42000", lxr.pos);
            return (SqlValue)cx.Add(left);
        }
        /// <summary>
        /// |	Value '||'  TypedValue 
        /// </summary>
        /// <param name="t">the expected data type</param>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the SqlValue</returns>
		SqlValue ParseSqlValueFactor(long qp,long xp,bool wfok)
		{
			var left = ParseSqlValueEntry(qp,xp,wfok);
			while (Match(Sqlx.CONCATENATE))
			{
				Sqlx op = tok;
				Next();
				var right = ParseSqlValueEntry(qp,left.defpos,wfok);
				left = new SqlValueExpr(lxr.Position, cx, op,left,right,Sqlx.NO);
			}
			return (SqlValue)cx.Add(left);
		}
        /// <summary>
        /// | 	Value '['  TypedValue ']'
        /// |	ColumnRef  
        /// | 	VariableRef 
        /// |   TypedValue
        /// |   PeriodRef | SYSTEM_TIME
        /// |   id ':'  Value
        /// |	Value '.' Member_id
        /// |   Value IN '(' Query | (Value {',' Value })')'
        /// |   Value IS [NOT] NULL
        /// |   Value IS [NOT] MEMBER OF Value
        /// |   Value IS [NOT] BETWEEN Value AND Value
        /// |   Value IS [NOT] OF '(' [ONLY] id {',' [ONLY] id } ')'
        /// |   Value IS [NOT] LIKE Value [ESCAPE TypedValue ]
        /// |   Value COLLATE id
        /// </summary>
        /// <param name="t">the expected data type</param>
        /// <param name="wfok">whether to allow a window function</param>
        /// <returns>the sqlValue</returns>
		SqlValue ParseSqlValueEntry(long qp,long xp,bool wfok)
        {
            var dt = ScalarXp(xp);
            var left = ParseSqlValueItem(qp,xp,wfok);
            bool invert = false;
            var lp = lxr.Position;
            if (left is SqlValue && tok == Sqlx.COLON)
            {
                var fl = left as SqlValue;
                if (fl == null)
                    throw new DBException("42000", left.ToString()).ISO();
                Next();
                left = ParseSqlValueItem(qp,xp,wfok);
                // ??
            }
            while (tok==Sqlx.DOT || tok==Sqlx.LBRACK)
                if (tok==Sqlx.DOT)
                {
                    // could be table alias, block id, instance id etc
                    Next();
                    if (tok == Sqlx.TIMES)
                    {
                        Next();
                        cx.stars += (cx.stars.Length,(cx.selectLength,left.defpos));
                        return null;
                    }
                    var n = new Ident(lxr,Sqlx.COLUMN);
                    Mustbe(Sqlx.ID);
                    if (tok == Sqlx.LPAREN)
                    {
                        Next();
                        var ps = ParseSqlValueList(qp,xp);
                        var ut = left.domain;
                        var ui = (ObInfo)cx.db.role.infos[ut?.defpos ?? -1L];
                        var ar = ps.Length;
                        var pr = cx.db.objects[ui?.methods[n.ident]?[ar] ?? -1L] as Method
                            ?? throw new DBException("42173", n);
                        var cs = new CallStatement(lp, pr, n.ident, ps, left);
                        Mustbe(Sqlx.RPAREN);
                        left = new SqlMethodCall(n.iix, cx, cs);
                    }
                    else
                    {
                        left = new SqlValueExpr(lp, cx, Sqlx.DOT, left,
                            new SqlValue(lxr.Position), Sqlx.NO);
                    }
                } else // tok==Sqlx.LBRACK
                {
                    Next();
                    left = new SqlValueExpr(lp, cx, Sqlx.LBRACK, left,
                        ParseSqlValue(qp,Domain.Int.defpos), Sqlx.NO);
                    Mustbe(Sqlx.RBRACK);
                }

            if (tok == Sqlx.IS)
            {
                if (!dt.CanTakeValueOf(cx, Domain.Bool))
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
                    bool only = (tok == Sqlx.ONLY);
                    if (only)
                        Next();
                    var r = BList<Domain>.Empty;
                    var t1 = ParseSqlDataType();
                    lp = lxr.Position;
                    if (only)
                        t1 = new Domain(lp,Sqlx.ONLY,new BTree<long,object>(Domain.Under,t1));
                    r+=t1;
                    while (tok == Sqlx.COMMA)
                    {
                        Next();
                        only = (tok == Sqlx.ONLY);
                        if (only)
                            Next();
                        t1 = ParseSqlDataType();
                        lp = lxr.Position;
                        if (only)
                            t1 = new Domain(lp,Sqlx.ONLY, new BTree<long, object>(Domain.Under, t1));
                        r+=t1;
                    }
                    Mustbe(Sqlx.RPAREN);
                    return (SqlValue)cx.Add(new TypePredicate(lp,left, b, r));
                }
                Mustbe(Sqlx.NULL);
                return (SqlValue)cx.Add(new NullPredicate(lp,left, b));
            }
            var savestart = lxr.start;
            if (tok == Sqlx.NOT)
            {
                if (!dt.CanTakeValueOf(cx,Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                invert = true;
            }
            if (tok == Sqlx.BETWEEN)
            {
                if (!dt.CanTakeValueOf(cx,Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                BetweenPredicate b = new BetweenPredicate(lp, left, !invert, 
                    ParseSqlValueTerm(qp,left.defpos,false), null);
                Mustbe(Sqlx.AND);
                b+=(QuantifiedPredicate.High,ParseSqlValue(qp,left.defpos));
                return (SqlValue)cx.Add(b);
            }
            if (tok == Sqlx.LIKE)
            {
                if (!(dt.CanTakeValueOf(cx,Domain.Bool) && 
                    Domain.Char.CanTakeValueOf(cx,left.domain)))
                    throw new DBException("42000", lxr.pos);
                Next();
                LikePredicate k = new LikePredicate(lp,left, !invert, 
                    ParseSqlValue(qp,Domain.Char.defpos), null);
                if (tok == Sqlx.ESCAPE)
                {
                    Next();
                    k+=(LikePredicate.Escape,ParseSqlValueItem(qp,Domain.Char.defpos, false));
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
                if (!dt.CanTakeValueOf(cx,Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                InPredicate n = new InPredicate(lp, left)+
                    (QuantifiedPredicate.Found, !invert);
                Mustbe(Sqlx.LPAREN);
                if (Match(Sqlx.SELECT, Sqlx.TABLE, Sqlx.VALUES))
                    n+=(QuantifiedPredicate.Select,cx.Add(ParseQuerySpecification(qp,xp)).defpos);
                else
                    n+=(QuantifiedPredicate.Vals,ParseSqlValueList(qp,left.defpos));
                Mustbe(Sqlx.RPAREN);
                return (SqlValue)cx.Add(n);
            }
            if (tok == Sqlx.MEMBER)
            {
                if (!dt.CanTakeValueOf(cx,Domain.Bool))
                    throw new DBException("42000", lxr.pos);
                Next();
                Mustbe(Sqlx.OF);
                var dm = cx.Add(new Domain(left.defpos, Sqlx.MULTISET, dt));
                return (SqlValue)cx.Add(new MemberPredicate(lxr.Position,left,
                    !invert, ParseSqlValue(qp,dm.defpos)));
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
            return (SqlValue)cx.Add(new SqlValueExpr(lxr.Position, cx,
                Sqlx.COLLATE, e, o.Build(e.defpos,cx,e.domain), Sqlx.NO));
        }
        /// <summary>
        ///  TypedValue= [NEW} MethodCall
        /// | 	FunctionCall 
        /// |	VALUES  '('  TypedValue { ','  TypedValue } ')' { ',' '('  TypedValue { ','  TypedValue } ')' }
        /// |	Subquery
        /// |	( MULTISET | ARRAY | ROW) '('  TypedValue { ','  TypedValue } ')'
        /// | 	TABLE '('  TypedValue ')' 
        /// |	TREAT '('  TypedValue AS Sub_Type ')'  
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
        /// The presence of the OVER keyword makes a window function. In accordance with SQL2003-02 section 4.15.3, window functions can only be used in the select list of a QuerySpecification or SelectSingle or the order by clause of a �simple table query� as defined in section 7.5 above. Thus window functions cannot be used within expressions or as function arguments.
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
        /// Provenance = PROVENANCE|TYPE_URI .
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
        /// <param name="t">the expected data type</param>
        /// <param name="wfok">whether a window function is allowed</param>
        /// <returns>the sql value</returns>
        SqlValue ParseSqlValueItem(long qp,long xp,bool wfok)
        {
            SqlValue r;
            if (tok == Sqlx.QMARK && cx.db.parse == ExecuteStatus.Prepare)
            {
                Next();
                var qm = new SqlValueExpr(lxr.Position, cx, Sqlx.QMARK, null, null, Sqlx.NO);
                cx.qParams += qm;
                return qm;
            }
            if (Match(Sqlx.LEVEL))
            {
                return (SqlValue)cx.Add(new SqlLiteral(lxr.Position, cx, TLevel.New(MustBeLevel())));
            }
            if (Match(Sqlx.HTTP))
            {
                Next();
                if (!Match(Sqlx.GET))
                { // supplying user and password this way is no longer supported
                    ParseSqlValue(qp,Domain.Char.defpos);
                    if (Match(Sqlx.COLON))
                    {
                        Next();
                        ParseSqlValue(qp,Domain.Char.defpos);
                    }
                }
                Mustbe(Sqlx.GET);
                var x = ParseSqlValue(qp,xp);
                string m = "application/json";
                if (Match(Sqlx.AS))
                {
                    Next();
                    m = lxr.val.ToString();
                    Mustbe(Sqlx.CHARLITERAL);
                }
                var wh = BTree<long,bool>.Empty;
                if (Match(Sqlx.WHERE))
                {
                    Next();
                    wh = ParseSqlValue(qp,Domain.Bool.defpos).Disjoin(cx);
        //            for (var b = wh.First(); b != null; b = b.Next())
       //                 lxr.context.cur.Needs(b.value().alias ?? b.value().name,Query.Need.condition);
                }
                return (SqlValue)cx.Add(new SqlHttp(lxr.Position, null, x, m, wh, "*"));
            }
            var lp = lxr.Position;
            Match(Sqlx.SCHEMA); // for Pyrrho 5.1 most recent schema change
            if (Match(Sqlx.ID,Sqlx.NEXT,Sqlx.LAST,Sqlx.CHECK,Sqlx.PROVENANCE,Sqlx.TYPE_URI)) // ID or pseudo ident
            {
                SqlValue vr = ParseVarOrColumn(qp,xp);
                if (tok == Sqlx.DOUBLECOLON)
                {
                    Next();
                    var ut = cx.db.GetObject(vr.name) as Domain
                        ?? throw new DBException("42139",vr.name).Mix();
                    var ui = cx.db.role.infos[vr.defpos] as ObInfo;
                    var name = new Ident(lxr,Sqlx.PROCEDURE);
                    Mustbe(Sqlx.ID);
                    lp = lxr.Position;
                    Mustbe(Sqlx.LPAREN);
                    var ps = ParseSqlValueList(qp,xp);
                    Mustbe(Sqlx.RPAREN);
                    int n = ps.Length;
                    var m = cx.db.objects[ui.methods[name.ident]?[n]??-1L] as Method
                        ?? throw new DBException("42132",name.ident,ui.name).Mix();
                    if (m.methodType != PMethod.MethodType.Static)
                        throw new DBException("42140").Mix();
                    var fc = new CallStatement(lp, m, name.ident, ps, vr);
                    return (SqlValue)cx.Add(new SqlProcedureCall(name.iix, cx, fc));
                }
                return (SqlValue)cx.Add(vr);
            }
            if (Match(Sqlx.EXISTS,Sqlx.UNIQUE))
            {
                var op0 = tok;
                Next();
                Mustbe(Sqlx.LPAREN);
                QueryExpression g = ParseQueryExpression(qp,Domain.Null.defpos);
                Mustbe(Sqlx.RPAREN);
                if (op0 == Sqlx.EXISTS)
                    return (SqlValue)cx.Add(new ExistsPredicate(lxr.Position, g));
                else
                    return (SqlValue)cx.Add(new UniquePredicate(lxr.Position, g));
            }
            if (Match(Sqlx.RDFLITERAL, Sqlx.DOCUMENTLITERAL, Sqlx.CHARLITERAL, 
                Sqlx.INTEGERLITERAL, Sqlx.NUMERICLITERAL,
            Sqlx.REALLITERAL, Sqlx.BLOBLITERAL, Sqlx.BOOLEANLITERAL))
            {
                r = lxr.val.Build(lxr.Position,cx,ScalarXp(xp));
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
                            lp = lxr.Position;
                            Next();
                            if (tok == Sqlx.SELECT)
                            {
                                var st = lxr.start;
                                var cs = ParseCursorSpecification(qp,Domain.Null.defpos).cs;
                                Mustbe(Sqlx.RPAREN);
                                return (SqlValue)cx.Add(new SqlValueSelect(lp, (Query)cx.obs[cs], 
                                    new string(lxr.input, st, lxr.start - st)));
                            }
                            throw new DBException("22204");
                        }
                        Mustbe(Sqlx.LBRACK);
                        var dm = ScalarXp(xp);
                        var et = (dm == Domain.Content) ? dm :
                            Domain.For(dm.elType?.prim??0) ??
                            throw new DBException("42000", lxr.pos);
                        var v = ParseSqlValueList(qp,et.defpos);
                        if (v.Length == 0)
                            throw new DBException("22103").ISO();
                        Mustbe(Sqlx.RBRACK);
                        return (SqlValue)cx.Add(new SqlValueArray(lp,cx,xp, v));
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
                            var tb = ob as Table;
                            if (tb == null)
                                throw new DBException("42107", n).Mix();
                            var ft = cx.db.role.infos[tb.defpos] as ObInfo;
                            ob = ft.ColFor(cx,cn.ToString());
                        }
                        r = new SqlLiteral(lp, cx, new TInt(ob.lastChange));
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(r);
                    } 
                case Sqlx.CURRENT_DATE: 
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp,cx,Sqlx.CURRENT_DATE, 
                            null, null,null,Sqlx.NO));
                    }
                case Sqlx.CURRENT_ROLE:
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp, cx,Sqlx.CURRENT_ROLE, 
                            null, null, null, Sqlx.NO));
                    }
                case Sqlx.CURRENT_TIME:
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp, cx,Sqlx.CURRENT_TIME, 
                            null, null, null, Sqlx.NO));
                    }
                case Sqlx.CURRENT_TIMESTAMP: 
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp, cx,Sqlx.CURRENT_TIMESTAMP, 
                            null, null, null, Sqlx.NO));
                    }
                case Sqlx.CURRENT_USER:
                    {
                        Next();
                        return (SqlValue)cx.Add(new SqlFunction(lp, cx,Sqlx.CURRENT_USER, 
                            null, null, null, Sqlx.NO));
                    }
                case Sqlx.DATE: // also TIME, TIMESTAMP, INTERVAL
                    {
                        Sqlx tk = tok;
                        Next();
                        var o = lxr.val;
                        lp = lxr.Position;
                        if (tok == Sqlx.CHARLITERAL)
                        {
                            Next();
                            if (xp > 0)
                                return new SqlDateTimeLiteral(lp,cx,ScalarXp(xp), o.ToString());
                            return (SqlValue)cx.Add(new SqlDateTimeLiteral(lp,cx, 
                                new Domain(lp,tk,BTree<long,object>.Empty), 
                                o.ToString()));
                        }
                        else
                            return (SqlValue)cx.Add(new SqlLiteral(lp,cx, o));
                    }
                case Sqlx.INTERVAL:
                    {
                        Next();
                        var o = lxr.val;
                        Mustbe(Sqlx.CHARLITERAL);
                        Domain di = ParseIntervalType();
                        return (SqlValue)cx.Add(new SqlDateTimeLiteral(lp, cx,di, o.ToString()));
                    }
                case Sqlx.LPAREN:// subquery
                    {
                        Mustbe(Sqlx.LPAREN);
                        if (tok == Sqlx.SELECT)
                        {
                            var st = lxr.start;
                            var cs = ParseCursorSpecification(qp,xp).cs;
                            Mustbe(Sqlx.RPAREN);
                            return (SqlValue)cx.Add(new SqlValueSelect(lp-1, (Query)cx.obs[cs], 
                                new string(lxr.input,st,lxr.start-st)));
                        }
                        Domain et = null;
                        var dm = cx.Dom(xp);
                        switch(dm.prim)
                        {
                            case Sqlx.ARRAY:
                            case Sqlx.MULTISET:
                                et = Domain.For(dm.elType.prim);
                                break;
                            case Sqlx.CONTENT:
                                et = Domain.Content;
                                break;
                        }
                        var fs = BList<SqlValue>.Empty;
                        for (var i = 0; ; i++)
                        {
                            var it = ParseSqlValue(qp,(et??dm[i]??Domain.Content).defpos);
                            if (tok == Sqlx.AS)
                            {
                                lp = lxr.Position;
                                Next();
                                var ic = new Ident(lxr,Sqlx.COLUMN);
                                Mustbe(Sqlx.ID);
                                it += (DBObject._Alias, ic.ToString());
                                cx.obs += (it.defpos, it);
                            }
                            fs += it;
                            if (tok != Sqlx.COMMA)
                                break;
                            Next();
                        }
                        Mustbe(Sqlx.RPAREN);
                        if (fs.Length == 1)
                            return (SqlValue)cx.Add(fs[0]);
                        if (dm.defpos<0) // this ad-hoc Row needs an ad-hoc ObInfo
                        {
                            var rs = BTree<long,Domain>.Empty;
                            var sq = 0;
                            for (var b = fs.First(); b != null; b = b.Next(), sq++)
                            {
                                var v = b.value();
                                rs += (v.defpos,v.domain);
                            }
                            dm = new Domain(lp, Domain.Row, rs);
                        }
                        return (SqlValue)cx.Add(new SqlRow(lp,cx,xp,fs));
                    }
                case Sqlx.MULTISET:
                    {
                        Next();
                        if (Match(Sqlx.LPAREN))
                            return ParseSqlValue(qp,xp);
                        Mustbe(Sqlx.LBRACK);
                        var v = ParseSqlValueList(qp,xp);
                        if (v.Length == 0)
                            throw new DBException("22103").ISO();
                        Domain dt = ((SqlValue)cx.obs[v[0].Item1]).domain;
                        Mustbe(Sqlx.RBRACK);
                        return (SqlValue)cx.Add(new SqlValueArray(lp, cx, xp, v));
                    }
                case Sqlx.NEW:
                    {
                        Next();
                        var o = new Ident(lxr,Sqlx.TYPE);
                        Mustbe(Sqlx.ID);
                        lp = lxr.Position;
                        var ut = cx.db.GetObject(o.ident) as Domain??
                            throw new DBException("42142").Mix();
                        Mustbe(Sqlx.LPAREN);
                        var ps = ParseSqlValueList(qp,ut.defpos);
                        int n = ps.Length;
                        Mustbe(Sqlx.RPAREN);
                        var ui = cx.db.role.infos[ut.defpos] as ObInfo;
                        Method m = cx.db.objects[ui.methods[o.ident]?[n]??-1L] as Method;
                        if (m == null)
                        {
                            if (ui.Length != 0 && ui.Length != n)
                                throw new DBException("42142").Mix();
                            return (SqlValue)cx.Add(new SqlDefaultConstructor(o.iix, cx, ut, ps));
                        }
                        if (m.methodType != PMethod.MethodType.Constructor)
                            throw new DBException("42142").Mix();
                        var fc = new CallStatement(lp, m, o.ident, ps);
                        return (SqlValue)cx.Add(new SqlProcedureCall(o.iix, cx, fc));
                    }
                case Sqlx.ROW:
                    {
                        Next();
                        if (Match(Sqlx.LPAREN))
                        {
                            lp = lxr.Position;
                            Next();
                            var v = ParseSqlValueList(qp,xp);
                            Mustbe(Sqlx.RPAREN);
                            return (SqlValue)cx.Add(new SqlRow(lp,cx,xp,v));
                        }
                        throw new DBException("42135", "ROW").Mix();
                    }
                /*       case Sqlx.SELECT:
                           {
                               var sc = new SaveContext(trans, ExecuteStatus.Parse);
                               Query cs = ParseCursorSpecification(t).stmt as Query;
                               sc.Restore(tr);
                               return (SqlValue)cx.Add(new SqlValueSelect(cs, t));
                           } */
                case Sqlx.TABLE: // allowed by 6.39
                    {
                        Next();
                        var lf = ParseSqlValue(qp,Domain.TableType.defpos);
                        return (SqlValue)cx.Add(lf);
                    }

                case Sqlx.TIME: goto case Sqlx.DATE;
                case Sqlx.TIMESTAMP: goto case Sqlx.DATE;
                case Sqlx.TREAT:
                    {
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        var v = ParseSqlValue(qp,Domain.Content.defpos);
                        Mustbe(Sqlx.RPAREN);
                        Mustbe(Sqlx.AS);
                        var dt = ParseSqlDataType();
                        return (SqlValue)cx.Add(new SqlTreatExpr(lp, v, dt, cx));//.Needs(v);
                    }
                case Sqlx.VALUE:
                    {
                        Next();
                        SqlValue vbl = new SqlValue(lp,"VALUE",ScalarXp(xp));
                        return (SqlValue)cx.Add(vbl);
                    }
                case Sqlx.VALUES:
                    {
                        var v = BList<long>.Empty;
                        Sqlx sep = Sqlx.COMMA;
                        while (sep == Sqlx.COMMA)
                        {
                            Next();
                            var llp = lxr.Position;
                            Mustbe(Sqlx.LPAREN);
                            var x = ParseSqlValueList(qp,xp);
                            Mustbe(Sqlx.RPAREN);
                            v+= cx.Add(new SqlRow(llp,cx,xp,x)).defpos;
                            sep = tok;
                        }
                        return (SqlValue)cx.Add(new SqlRowArray(lp, cx, xp, v));
                    }
                case Sqlx.LBRACE:
                    {
                        var v = new SqlRow(lp,BTree<long,object>.Empty);
                        Next();
                        if (tok != Sqlx.RBRACE)
                            GetDocItem(cx,v);
                        while (tok==Sqlx.COMMA)
                        {
                            Next();
                            GetDocItem(cx,v);
                        }
                        Mustbe(Sqlx.RBRACE);
                        return (SqlValue)cx.Add(v);
                    }
                case Sqlx.LBRACK:
                        return (SqlValue)cx.Add(ParseSqlDocArray());
                case Sqlx.LSS:
                    return (SqlValue)cx.Add(ParseXmlValue(qp));
            }
            // "SQLFUNCTIONS"
            Sqlx op;
            SqlValue val = null;
            SqlValue op1 = null;
            SqlValue op2 = null;
            SqlValue filter = null;
            Sqlx mod = Sqlx.NO;
            WindowSpecification window = null;
            Ident windowName = null;
            lp = lxr.Position;
            switch (tok)
            {
                case Sqlx.ABS:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(qp,Domain.UnionNumeric.defpos);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.ANY: goto case Sqlx.COUNT;
                case Sqlx.AVG: goto case Sqlx.COUNT;
                case Sqlx.CARDINALITY: // multiset arg functions
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(qp,Domain.Collection.defpos);
                        if (op != Sqlx.MULTISET)
                            throw new DBException("42113", op).Mix();
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.CASE:
                    {
                        op = tok;
                        Next();
                        if (tok == Sqlx.WHEN) // searched case
                        {
                            while (tok == Sqlx.WHEN)
                            {
                                op = tok;
                                Next();
                                var llp = lxr.Position;
                                op1 = ParseSqlValue(qp,Domain.Bool.defpos);
                                Mustbe(Sqlx.THEN);
                                val = ParseSqlValue(qp,xp);
                                if (tok == Sqlx.WHEN)
                                    op2 = new SqlFunction(llp, cx, op,val, op1, op2, mod);
                            }
                            if (tok == Sqlx.ELSE)
                            {
                                Next();
                                op2 = ParseSqlValue(qp,xp);
                            }
                            Mustbe(Sqlx.END);
                            return (SqlValue)cx.Add(new SqlFunction(lp,cx, op,val,op1,op2,mod));
                        }
                        var val1 = ParseSqlValue(qp,Domain.Content.defpos); // simple case
                        if (tok != Sqlx.WHEN)
                            throw new DBException("42129").Mix();
                        while (tok == Sqlx.WHEN)
                        {
                            Next();
                            var llp = lxr.Position;
                            op1 = ParseSqlValue(qp,val1.defpos);
                            var c = new List<SqlValue>();
                            while (tok == Sqlx.COMMA)
                            {
                                Next();
                                c.Add(ParseSqlValue(qp,val1.defpos));
                            }
                            Mustbe(Sqlx.THEN);
                            val = ParseSqlValue(qp,xp);
                            foreach(var cv in c)
                                op2 = new SqlFunction(llp, cx, Sqlx.WHEN,val,cv,op2,mod);
                        }
                        if (tok == Sqlx.ELSE)
                            op2 = ParseSqlValue(qp,xp);
                        val = val1;
                        Mustbe(Sqlx.END);
                        break;
                    }
                case Sqlx.CAST:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(qp,Domain.Content.defpos);
                        Mustbe(Sqlx.AS);
                        op1 = new SqlTypeExpr(lp,cx,ParseSqlDataType());
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.CEIL: goto case Sqlx.ABS;
                case Sqlx.CEILING: goto case Sqlx.ABS;
                case Sqlx.CHAR_LENGTH:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(qp,Domain.Char.defpos);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.CHARACTER_LENGTH: goto case Sqlx.CHAR_LENGTH;
                case Sqlx.COALESCE:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        op1 = ParseSqlValue(qp,xp);
                        Mustbe(Sqlx.COMMA);
                        op2 = ParseSqlValue(qp,xp);
                        while (tok == Sqlx.COMMA)
                        {
                            Next();
                            op1 = new SqlCoalesce(lxr.Position, cx,op1,op2);
                            op2 = ParseSqlValue(qp,xp);
                        }
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(new SqlCoalesce(lp, cx, op1, op2));
                    }
                case Sqlx.COLLECT: goto case Sqlx.COUNT;
#if OLAP
                case Sqlx.CORR: goto case Sqlx.COVAR_POP;
#endif
                case Sqlx.COUNT: // actually a special case: but deal with all ident-arg aggregates here
                    {
                        op = tok;
                        mod = Sqlx.NO; // harmless default value
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        if (op == Sqlx.COUNT && tok == Sqlx.TIMES)
                        {
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
                            val = ParseSqlValue(qp,Domain.Content.defpos);
                        }
                        Mustbe(Sqlx.RPAREN);
                        if (tok == Sqlx.FILTER)
                        {
                            Next();
                            Mustbe(Sqlx.LPAREN);
                            Mustbe(Sqlx.WHERE);
                            filter = ParseSqlValue(qp,Domain.Bool.defpos);
                            Mustbe(Sqlx.RPAREN);
                        }
                        if (tok == Sqlx.OVER && wfok)
                        {
                            Next();
                            if (tok == Sqlx.ID)
                            {
                                windowName = new Ident(lxr,Sqlx.WINDOW);
                                Next();
                            }
                            else
                            {
                                window = ParseWindowSpecificationDetails(qp);
                                window += (Basis.Name, "U" + cx.db.uid);
                            }
                        }
                        var sf = new SqlFunction(lp, cx, op, val, op1, op2, mod, BTree<long, object>.Empty
                            + (SqlFunction.Filter, filter) + (SqlFunction.Window, window)
                            + (SqlFunction.WindowId, windowName));
                        return (SqlValue)cx.Add(sf);
                    }
#if OLAP
                case Sqlx.COVAR_POP:
                    {
                        QuerySpecification se = cx as QuerySpecification;
                        if (se != null)
                            se.aggregates = true;
                        op = tok;
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
                        op = tok;
                        Next();
                        Mustbe(Sqlx.OF);
                        val = (SqlValue)cx.Get(ParseIdentChain(),xp);
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
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        mod = tok;
                        Mustbe(Sqlx.YEAR, Sqlx.MONTH, Sqlx.DAY, Sqlx.HOUR, Sqlx.MINUTE, Sqlx.SECOND);
                        Mustbe(Sqlx.FROM);
                        val = ParseSqlValue(qp,Domain.UnionDate.defpos);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.FLOOR: goto case Sqlx.ABS;
                case Sqlx.FUSION: goto case Sqlx.COUNT;
                case Sqlx.GROUPING:
                    {
                        Next();
                        var cs = BList<long>.Empty;
                        var ids = ParseIDList(Sqlx.COLUMN);
                        for (var i = 0; i < ids.Length; i++)
                            cs += ids[i].iix;
                        return (SqlValue)cx.Add(new ColumnFunction(lp, cx, cs));
                    }
                case Sqlx.INTERSECT: goto case Sqlx.COUNT;
                case Sqlx.LN: goto case Sqlx.ABS;
                case Sqlx.LOWER: goto case Sqlx.SUBSTRING;
                case Sqlx.MAX: 
                case Sqlx.MIN:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(qp,Domain.UnionDateNumeric.defpos);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.MOD: goto case Sqlx.NULLIF;
                case Sqlx.NULLIF:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        op1 = ParseSqlValue(qp,Domain.Content.defpos);
                        Mustbe(Sqlx.COMMA);
                        op2 = ParseSqlValue(qp,op1.defpos);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
#if SIMILAR
                case Sqlx.OCCURRENCES_REGEX:
                    {
                        op = tok;
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
                        op = tok;
                        Next();
                        break;
                    }
#if OLAP
                case Sqlx.PERCENT_RANK: goto case Sqlx.RANK;
                case Sqlx.PERCENTILE_CONT:
                    {
                        op = tok;
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
                        op = tok;
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
                        op = tok;
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
                        op = tok;
                        Next();
                        lp = lxr.Position;
                        Mustbe(Sqlx.LPAREN);
                        if (tok == Sqlx.RPAREN)
                        {
                            Next();
                            Mustbe(Sqlx.OVER);
                            if (tok == Sqlx.ID)
                            {
                                windowName = new Ident(lxr,Sqlx.WINDOW);
                                Next();
                            }
                            else
                            {
                                window = ParseWindowSpecificationDetails(qp);
                                window+=(Basis.Name,"U"+ cx.db.uid);
                            }
                            return (SqlValue)cx.Add(new SqlFunction(lp, cx, op, val, op1, op2, mod, BTree<long,object>.Empty
                            +(SqlFunction.Filter,filter)+(SqlFunction.Window,window)
                             +(SqlFunction.WindowId,windowName)));
                        }
                        var dm = ScalarXp(xp);
                        var x = cx.Add(ParseSqlValue(qp, dm.defpos));
                        var v = new RowType(x);
                        for (var i=1; tok == Sqlx.COMMA;i++)
                        {
                            Next();
                            x = cx.Add(ParseSqlValue(qp, dm.defpos));
                            v += (x.defpos,x.domain);
                        }
                        Mustbe(Sqlx.RPAREN);
                        val = new SqlRow(lxr.Position, cx, xp, v);
                        var f = new SqlFunction(lp, cx, op, val, op1, op2, mod, BTree<long, object>.Empty
                            + (SqlFunction.Window, ParseWithinGroupSpecification(qp))
                            + (SqlFunction.WindowId,"U"+ cx.db.uid));
                        return (SqlValue)cx.Add(f);
                    }
                case Sqlx.ROWS: // Pyrrho (what is this?)
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        if (Match(Sqlx.TIMES))
                        {
                            mod = Sqlx.TIMES;
                            Next();
                        }
                        else
                            val = ParseSqlValue(qp,Domain.Int.defpos);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.SET: goto case Sqlx.CARDINALITY;
                case Sqlx.SOME: goto case Sqlx.COUNT;
                case Sqlx.SQRT: goto case Sqlx.ABS;
                case Sqlx.STDDEV_POP: goto case Sqlx.COUNT;
                case Sqlx.STDDEV_SAMP: goto case Sqlx.COUNT;
                case Sqlx.SUBSTRING:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(qp,Domain.Char.defpos);
                        if (op == Sqlx.SUBSTRING)
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
                                op1 = ParseSqlValue(qp,Domain.Int.defpos);
                                if (tok == Sqlx.FOR)
                                {
                                    Next();
                                    op2 = ParseSqlValue(qp,Domain.Int.defpos);
                                }
                            }
                        }
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
#if SIMILAR
                case Sqlx.SUBSTRING_REGEX:
                    {
                        op = tok;
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
                        op = tok;
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
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        if (Match(Sqlx.LEADING, Sqlx.TRAILING, Sqlx.BOTH))
                        {
                            mod = tok;
                            Next();
                        }
                        val = ParseSqlValue(qp,Domain.Char.defpos);
                        if (tok == Sqlx.FROM)
                        {
                            Next();
                            op1 = val; // trim character
                            val = ParseSqlValue(qp,Domain.Char.defpos);
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
                    op = tok;
                    Next();
                    break;
                case Sqlx.XMLAGG: goto case Sqlx.COUNT;
                case Sqlx.XMLCAST: goto case Sqlx.CAST;
                case Sqlx.XMLCOMMENT:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(qp,Domain.Char.defpos);
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }
                case Sqlx.XMLCONCAT:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        val = ParseSqlValue(qp,Domain.Char.defpos);
                        while (tok == Sqlx.COMMA)
                        {
                            Next();
                            val = new SqlValueExpr(lxr.Position, cx, Sqlx.XMLCONCAT, 
                                val, ParseSqlValue(qp,Domain.Char.defpos), Sqlx.NO);
                        }
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(val);
                    }
                case Sqlx.XMLELEMENT:
                    {
                        op = tok;
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
                            var llp = lxr.Position;
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
                                var doc = new SqlRow(llp,BTree<long,object>.Empty);
                                var v = ParseSqlValue(qp,Domain.Char.defpos);
                                var j = 0;
                                var a = new Ident("Att"+(++j),0, Sqlx.VALUE);
                                if (tok == Sqlx.AS)
                                {
                                    Next();
                                    a = new Ident(lxr, Sqlx.VALUE);
                                    Mustbe(Sqlx.ID);
                                }
                                doc += v+(Basis.Name,a.ident);
                                a = new Ident("Att" + (++j),0,Sqlx.VALUE);
                                while (tok == Sqlx.COMMA)
                                {
                                    Next();
                                    var w = ParseSqlValue(qp,Domain.Char.defpos);
                                    if (tok == Sqlx.AS)
                                    {
                                        Next();
                                        a = new Ident(lxr,Sqlx.VALUE);
                                        Mustbe(Sqlx.ID);
                                    }
                                }
                                doc += v + (Basis.Name, a.ident);
                                v = new SqlValueExpr(lp, cx, Sqlx.XMLATTRIBUTES, v, null, Sqlx.NO);
                                Mustbe(Sqlx.RPAREN);
                                op2 = v;
                            }
                            else
                            {
                                val = new SqlValueExpr(lp, cx, Sqlx.XML, val, 
                                    ParseSqlValue(qp,Domain.Char.defpos), Sqlx.NO);
                                n++;
                            }
                        }
                        Mustbe(Sqlx.RPAREN);
                        op1 = new SqlLiteral(lxr.Position,cx,name);
                        break;
                    }
                case Sqlx.XMLFOREST:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        if (tok == Sqlx.XMLNAMESPACES)
                        {
                            Next();
                            ParseXmlNamespaces();
                        }
                        val = ParseSqlValue(qp,Domain.Char.defpos);
                        while (tok == Sqlx.COMMA)
                        {
                            var llp = lxr.Position;
                            Next();
                            val = new SqlValueExpr(llp, cx, Sqlx.XMLCONCAT, val, 
                                ParseSqlValue(qp,Domain.Char.defpos), Sqlx.NO);
                        }
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(val);
                    }
                case Sqlx.XMLPARSE:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        Mustbe(Sqlx.CONTENT);
                        val = ParseSqlValue(qp,Domain.Char.defpos);
                        Mustbe(Sqlx.RPAREN);
                        return (SqlValue)cx.Add(val);
                    }
                case Sqlx.XMLQUERY:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        op1 = ParseSqlValue(qp,Domain.Char.defpos);
                        if (tok != Sqlx.COMMA)
                            throw new DBException("42000", tok).Mix();
                        lxr.XmlNext(')');
                        op2 = new SqlLiteral(lxr.Position, cx, new TChar(lxr.val.ToString()));
                        Next();
                        break;
                    }
                case Sqlx.XMLPI:
                    {
                        op = tok;
                        Next();
                        Mustbe(Sqlx.LPAREN);
                        Mustbe(Sqlx.NAME);
                        val = new SqlLiteral(lxr.Position, cx, new TChar( lxr.val.ToString()));
                        Mustbe(Sqlx.ID);
                        if (tok == Sqlx.COMMA)
                        {
                            Next();
                            op1 = ParseSqlValue(qp,Domain.Char.defpos);
                        }
                        Mustbe(Sqlx.RPAREN);
                        break;
                    }

                default:
                    return (SqlValue)cx.Add(new SqlProcedureCall(lp,cx,
                        (CallStatement)ParseProcedureCall(xp)));
            }
            return (SqlValue)cx.Add(new SqlFunction(lp, cx, op, val, op1, op2, mod));
        }

        /// <summary>
        /// WithinGroup = WITHIN GROUP '(' OrderByClause ')' .
        /// </summary>
        /// <returns>A WindowSpecification</returns>
        WindowSpecification ParseWithinGroupSpecification(long qp)
        {
            WindowSpecification r = new WindowSpecification(lxr.Position);
            Mustbe(Sqlx.WITHIN);
            Mustbe(Sqlx.GROUP);
            Mustbe(Sqlx.LPAREN);
            r+=(WindowSpecification.Order,OrderSpec.Empty
                +(OrderSpec.Items,ParseOrderClause(qp,r.order,false)));
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
            var pn = new XmlNameSpaces(lxr.Position);
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
		WindowSpecification ParseWindowSpecificationDetails(long qp)
		{
			Mustbe(Sqlx.LPAREN);
			WindowSpecification w = new WindowSpecification(lxr.Position);
            if (tok == Sqlx.ID)
            {
                w+=(WindowSpecification.OrderWindow,lxr.val.ToString());
                Next();
            }
            var oi = RowType.Empty;
            int pt;
			if (tok==Sqlx.PARTITION)
			{
				Next();
				Mustbe(Sqlx.BY);
                oi = ParseSqlValueList(qp,Domain.Char.defpos);
                pt = (int)oi.Count;
                w += (WindowSpecification.Order, OrderSpec.Empty + (OrderSpec.Items, oi));
                w += (WindowSpecification.Partition, pt);
			}
			if (tok==Sqlx.ORDER)
				w +=(WindowSpecification.Order,OrderSpec.Empty
                    +(OrderSpec.Items,ParseOrderClause(qp,OrderSpec.Empty,false)));
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
			return w;
		}
        /// <summary>
		/// WindowBound = WindowStart | ((TypedValue | UNBOUNDED) FOLLOWING ) .
        /// </summary>
        /// <returns>The WindowBound</returns>
        WindowBound ParseWindowBound()
        {
            bool prec = false,unbd = true;
            Domain tp = Domain.Int;
            TypedValue d = null;
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
                var lp = lxr.Position;
                Mustbe(Sqlx.CHAR);
                Domain di = ParseIntervalType();
                d = di.Parse(new Scanner(lp,o.ToString().ToCharArray(),0));
                tp = di;
                unbd = false;
            }
            else
            {
                d = lxr.val;
                Sqlx tk = Mustbe(Sqlx.INTEGERLITERAL, Sqlx.NUMERICLITERAL);
                tp = Domain.Predefined(tk);
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
        internal SqlValue ParseSqlValueItem(string sql,long xp)
        {
            lxr = new Lexer(sql);
            tok = lxr.tok;
            if (lxr.Position > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId + sql.Length); // not really needed here
            return ParseSqlValueItem(-1,xp,false);
        }
        /// <summary>
        /// For the REST service there may be an explicit procedure call
        /// </summary>
        /// <param name="sql">a call statement to parse</param>
        /// <returns>the CallStatement</returns>
        internal CallStatement ParseProcedureCall(string sql,long xp)
        {
            lxr = new Lexer(sql); 
            tok = lxr.tok;
            if (lxr.Position > Transaction.TransPos)  // if sce is uncommitted, we need to make space above nextIid
                cx.db += (Database.NextId, cx.db.nextId +sql.Length); // not really needed here
            var n = new Ident(lxr,Sqlx.PROCEDURE);
            Mustbe(Sqlx.ID);
            var ps = RowType.Empty;  // actuals
            var lp = lxr.Position;
            if (tok == Sqlx.LPAREN)
            {
                Next();
                ps = ParseSqlValueList(xp,-1L);
            }
            var arity = (int)ps.Count;
            Mustbe(Sqlx.RPAREN);
            var pp = cx.db.role.procedures[n.ident]?[arity] ?? -1;
            var pr = cx.db.objects[pp] as Procedure
                ?? throw new DBException("42108", n).Mix();
            var fc = new CallStatement(lp, pr, n.ident, ps);
            return (CallStatement)cx.Add(fc);
        }
        /// <summary>
		/// UserFunctionCall = Id '(' [  TypedValue {','  TypedValue}] ')' .
        /// </summary>
        /// <param name="t">the expected data type</param>
        /// <returns>the Executable</returns>
        Executable ParseProcedureCall(long xp)
        {
            var id = new Ident(lxr,Sqlx.PROCEDURE);
            Mustbe(Sqlx.ID);
            var lp = lxr.Position;
            Mustbe(Sqlx.LPAREN);
            var ps = ParseSqlValueList(-1L,xp);
            var a = ps.Length;
            Mustbe(Sqlx.RPAREN);
            var pp = cx.db.role.procedures[id.ident]?[a] ??
                throw new DBException("42108", id.ident).Mix();
            var pr = (Procedure)cx.db.objects[pp];
            var fc = new CallStatement(lp, pr, id.ident, ps);
            return (Executable)cx.Add(fc);
        }
        /// <summary>
        /// Parse a list of Sql values
        /// </summary>
        /// <param name="t">the expected data type</param>
        /// <returns>the List of SqlValue</returns>
        RowType ParseSqlValueList(long qp,long xp)
        {
            var r = RowType.Empty;
            Domain ei = null;
            var dt = ScalarXp(xp);
            var sel = cx._RowType(xp); 
            switch (dt.prim)
            {
                case Sqlx.ARRAY:
                case Sqlx.MULTISET:
                    ei = Domain.For(dt.elType.prim);
                    break;
                default:
                    if (dt.Length == 0)
                        ei = dt;
                    break;
            }
            for (var b=sel.First(); ;b=b?.Next())
            {
                var v = ParseSqlValue(qp,ei?.defpos??b?.value().Item1??-1L);
                cx.Add(v);
                if (tok == Sqlx.AS)
                {
                    Next();
                    var d = ParseSqlDataType();
                    v = new SqlTreatExpr(lxr.Position, v, d, cx); //.Needs(v);
                    cx.Add(v);
                }
                r += (v.defpos,v.domain);
                if (tok == Sqlx.COMMA)
                    Next();
                else
                    break;
            }
            return r;
        }
        public SqlRow ParseSqlRow(long qp,long xp)
        {
            Mustbe(Sqlx.LPAREN);
            var lk = ParseSqlValueList(qp,xp);
            Mustbe(Sqlx.RPAREN);
            return (SqlRow)cx.Add(new SqlRow(lxr.Position, cx, xp, lk));
        }
        /// <summary>
        /// Parse an SqlRow
        /// </summary>
        /// <param name="db">the database</param>
        /// <param name="sql">The string to parse</param>
        /// <param name="result">the expected data type</param>
        /// <returns>the SqlRow</returns>
        public SqlValue ParseSqlValueList(string sql,long xp)
        {
            lxr = new Lexer(sql);
            tok = lxr.tok;
            if (tok == Sqlx.LPAREN)
                return ParseSqlRow(-1,xp);         
            return ParseSqlValueEntry(-1,xp,false);
        }
        /// <summary>
        /// Get a document item
        /// </summary>
        /// <param name="v">The document being constructed</param>
        SqlRow GetDocItem(Context cx, SqlRow v)
        {
            Ident k = new Ident(lxr,Sqlx.CONTENT);
            Mustbe(Sqlx.ID);
            Mustbe(Sqlx.COLON);
            return v + (ParseSqlValue(-1,Domain.Content.defpos) +(Basis.Name,k.ident));
        }
        /// <summary>
        /// Parse a document array
        /// </summary>
        /// <returns>the SqlDocArray</returns>
        public SqlValue ParseSqlDocArray()
        {
            var v = new SqlRowArray(lxr.Position,BTree<long,object>.Empty);
            cx.Add(v);
            Next();
            if (tok != Sqlx.RBRACK)
                v += ParseSqlRow(-1,Domain.Content.defpos);
            while (tok == Sqlx.COMMA)
            {
                Next();
                v+=ParseSqlRow(-1,Domain.Content.defpos);
            }
            Mustbe(Sqlx.RBRACK);
            return (SqlValue)cx.Add(v);
        }
        /// <summary>
        /// Parse an XML value
        /// </summary>
        /// <returns>the SqlValue</returns>
        public SqlValue ParseXmlValue(long qp)
        {
            Mustbe(Sqlx.LSS);
            var e = GetName();
            var v = new SqlXmlValue(lxr.Position,cx,e,SqlNull.Value,BTree<long,object>.Empty);
            cx.Add(v);
            while (tok!=Sqlx.GTR && tok!=Sqlx.DIVIDE)
            {
                var a = GetName();
                v+=(SqlXmlValue.Attrs,v.attrs+(a, cx.Add(ParseSqlValue(qp,Domain.Char.defpos)).defpos));
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
                        v+=(SqlXmlValue.Content,new SqlLiteral(lxr.Position, cx,
                            new TChar(new string(lxr.input, st, lxr.start - st))));
                    }
                    else
                    {
                        lxr.PushBack(Sqlx.ANY);
                        lxr.pos = lxr.start;
                        v += (SqlXmlValue.Content, ParseSqlValueItem(qp,Domain.Char.defpos, false));
                    }
                }
                else
                    while (tok != Sqlx.DIVIDE) // tok should Sqlx.LSS
                        v+=(SqlXmlValue.Children,v.children+(cx.Add(ParseXmlValue(qp)).defpos));
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
                e.prefix = e.keyname;
                e.keyname = new string(lxr.input, lxr.start, lxr.pos - lxr.start);
                Mustbe(Sqlx.ID);
            }
            return e;
        }
        BTree<long,bool> _Deps(BList<long> bl)
        {
            var r = BTree<long, bool>.Empty;
            for (var b = bl.First(); b != null; b = b.Next())
                r += (b.value(), true);
            return r;
        }
    }
}