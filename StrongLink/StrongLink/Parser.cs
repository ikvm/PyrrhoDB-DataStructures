﻿using System;
using Shareable;
#nullable enable
namespace StrongLink
{
    public class Parser
    {
        internal enum Sym
        {
            Null = 0,
            ID = 1,
            LITERAL = 2,
            LPAREN = 3,
            COMMA = 4,
            RPAREN = 5,
            EQUAL = 6, // EQUAL to GTR must be adjacent
            NEQ = 7,
            LEQ = 8,
            LSS = 9,
            GEQ = 10,
            GTR = 11,
            DOT = 12,
            PLUS = 13,
            MINUS = 14,
            TIMES = 15,
            DIVIDE = 16,
            //=== RESERVED WORDS
            ADD = 17,
            ALTER = 18,
            AND = 19,
            AS = 20,
            BEGIN = 21,
            BOOLEAN = 22,
            COLUMN = 23,
            COMMIT = 24,
            COUNT = 25,
            CREATE = 26,
            DATE = 27,
            DELETE = 28,
            DESC = 29,
            DISTINCT = 30,
            DROP = 31,
            FALSE = 32,
            FOR = 33,
            FROM = 34,
            INDEX = 35,
            INSERT = 36,
            INTEGER = 37,
            IN = 38,
            IS = 39,
            MAX = 40,
            MIN = 41,
            NOT = 42,
            NULL = 43,
            NUMERIC = 44,
            OR = 45,
            ORDERBY = 46,
            PRIMARY = 47,
            REFERENCES = 48,
            ROLLBACK = 49,
            SELECT = 50,
            SET = 51,
            STRING = 52,
            SUM = 53,
            TABLE = 54,
            TIMESPAN = 55,
            TIMESTAMP = 56,
            TO = 57,
            TRUE = 58,
            UPDATE = 59,
            VALUES = 60,
            WHERE = 61
        }
        internal class Lexer
        {
            public readonly char[] input;
            int pos = -1;
            int? pushPos = null;
            internal Sym tok;
            internal Sym pushBack = Sym.Null;
            internal Serialisable val = Serialisable.Null;
            char ch = '\0';
            char? pushCh = null;
            public Lexer(string inp)
            {
                input = inp.ToCharArray();
                Advance();
                tok = Next();
            }
            internal char Advance()
            {
                if (pos >= input.Length)
                    throw new Exception("Non-terminated string");
                ch = (++pos >= input.Length) ? '\0' : input[pos];
                return ch;
            }
            internal Sym PushBack(Sym old)
            {
                pushBack = old;
                pushCh = ch;
                pushPos = pos;
                tok = old;
                return tok;
            }
            Integer Unsigned()
            {
                var v = new Integer(ch - '0');
                for (Advance(); ch != '\0' && char.IsDigit(ch); Advance())
                    v = v.Times(10) + new Integer(ch - '0');
                return v;
            }
            Integer Unsigned(Integer v)
            {
                for (Advance(); ch != '\0' && char.IsDigit(ch); Advance())
                    v = v.Times(10) + new Integer(ch - '0');
                return v;
            }
            int Unsigned(int n)
            {
                var st = pos;
                var r = Unsigned();
                if (pos != st + n)
                    throw new Exception("Expected " + n + " digits");
                return (int)r;
            }
            int Unsigned(int n, int low, int high)
            {
                var r = Unsigned(n);
                if (r < low || r > high)
                    throw new Exception("Expected " + low + "<=" + r + "<=" + high);
                return r;
            }
            void Mustbe(char c)
            {
                if (c != ch)
                    throw new Exception("Expected " + c + " got " + ch);
                Advance();
            }
            Serialisable DateTimeLiteral()
            {
                var st = pos;
                var y = Unsigned(4);
                Mustbe('-');
                var mo = Unsigned(2, 1, 12);
                Mustbe('-');
                var d = Unsigned(2, 1, 31);
                if (ch == '\'')
                {
                    Advance();
                    return new SDate(new DateTime(y, mo, d));
                }
                Mustbe(' ');
                var h = Unsigned(2, 0, 23);
                Mustbe(':');
                var mi = Unsigned(2, 0, 59);
                Mustbe(':');
                var s = Unsigned(2, 0, 59);
                Mustbe('\'');
                var dt = new DateTime(y, mo, d, h, mi, s);
                return new STimestamp(dt);
            }
            Sym For(Types t)
            {
                switch (t)
                {
                    case Types.SDate: return Sym.DATE;
                    case Types.STimestamp: return Sym.TIMESTAMP;
                }
                throw new Exception("Unexpected type " + t);
            }
            internal Sym Next()
            {
                if (pushBack!=Sym.Null)
                {
                    tok = pushBack;
                    pos = pushPos.Value;
                    ch = pushCh.Value;
                    pushBack = Sym.Null;
                    return tok;
                }
                while (char.IsWhiteSpace(ch))
                    Advance();
                var st = pos;
                if (ch == '\0')
                    return tok=Sym.Null;
                if (char.IsDigit(ch))
                {
                    var n = Unsigned();
                    if (ch == '.')
                    {
                        var p = pos;
                        var m = Unsigned(n);
                        val = new SNumeric(m, pos - p, pos - p);
                        return tok = Sym.LITERAL;
                    }
                    val = new SInteger(n);
                    return tok = Sym.LITERAL;
                }
                else if (ch == '\'')
                {
                    st++;
                    for (Advance(); ch != '\0' && ch != '\''; Advance())
                        ;
                    if (ch == '\0')
                        throw new Exception("non-terminated string literal");
                    Advance();
                    val = new SString(new string(input, st, pos - st - 1));
                    return tok = Sym.LITERAL;
                }
                else if (char.IsLetter(ch))
                {
                    for (Advance(); char.IsLetterOrDigit(ch) || ch == '_'; Advance())
                        ;
                    var s = new string(input, st, pos - st);
                    var su = s.ToUpper();
                    for (var t = Sym.ADD; t <= Sym.WHERE; t++)
                        if (su.CompareTo(t.ToString()) == 0)
                            switch (t)
                            {
                                case Sym.DATE:
                                    if (ch == '\'')
                                    {
                                        Advance();
                                        val = DateTimeLiteral();
                                        return tok = Sym.LITERAL;
                                    }
                                    return tok = t;
                                case Sym.TIMESPAN:
                                    if (ch == '\'')
                                    {
                                        Advance();
                                        val = new STimeSpan(new TimeSpan(Unsigned()));
                                        if (ch != '\'')
                                            throw new Exception("non-terminated string literal");
                                        Advance();
                                        return tok = Sym.LITERAL;
                                    }
                                    return tok = t;
                                case Sym.FALSE: val = SBoolean.False;
                                    return tok = Sym.LITERAL;
                                case Sym.TRUE:
                                    val = SBoolean.True;
                                    return tok = Sym.LITERAL;
                                default:
                                    return tok = t;
                            }
                    val = new SString(s);
                    return tok = Sym.ID;
                }
                else
                    switch (ch)
                    {
                        case '.': Advance(); return tok = Sym.DOT;
                        case '+': Advance(); return tok = Sym.PLUS;
                        case '-': Advance(); return tok = Sym.MINUS;
                        case '*': Advance(); return tok = Sym.TIMES;
                        case '/': Advance(); return tok = Sym.DIVIDE;
                        case '(': Advance(); return tok = Sym.LPAREN;
                        case ',': Advance(); return tok = Sym.COMMA;
                        case ')': Advance(); return tok = Sym.RPAREN;
                        case '=': Advance(); return tok = Sym.EQUAL;
                        case '!':
                            Advance();
                            if (ch == '=')
                            {
                                Advance();
                                return tok = Sym.NEQ;
                            }
                            else break;
                        case '<':
                            Advance();
                            if (ch == '=')
                            {
                                Advance();
                                return tok = Sym.LEQ;
                            }
                            return tok = Sym.LSS;
                        case '>':
                            Advance();
                            if (ch == '=')
                            {
                                Advance();
                                return tok = Sym.GEQ;
                            }
                            return tok = Sym.GTR;
                    }
                throw new Exception("Bad input " + ch + " at " + pos);
            }
        }
        Lexer lxr;
        Parser(string inp)
        {
            lxr = new Lexer(inp);
        }
        public static Serialisable Parse(string sql)
        {
            return new Parser(sql).Statement();
        }
        Sym Next()
        {
            return lxr.Next();
        }
        void Mustbe(Sym t)
        {
            if (lxr.tok != t)
                throw new Exception("Syntax error: " + lxr.tok.ToString());
            Next();
        }
        SString MustBeID()
        {
            var s = lxr.val;
            if (lxr.tok != Sym.ID || s==null)
                throw new Exception("Syntax error: " + lxr.tok.ToString());
            Next();
            return (SString)s;
        }
        Types For(Sym t)
        {
            switch (t)
            {
                case Sym.TIMESTAMP: return Types.STimestamp;
                case Sym.INTEGER: return Types.SInteger;
                case Sym.NUMERIC: return Types.SNumeric;
                case Sym.STRING: return Types.SString;
                case Sym.DATE: return Types.SDate;
                case Sym.TIMESPAN: return Types.STimeSpan;
                case Sym.BOOLEAN: return Types.SBoolean;
            }
            throw new Exception("Syntax error: " + t);
        }
        public Serialisable Statement()
        {
            switch(lxr.tok)
            {
                case Sym.ALTER:
                    return Alter();
                case Sym.CREATE:
                    {
                        Next();
                        switch (lxr.tok)
                        {
                            case Sym.TABLE:
                                return CreateTable();
                            case Sym.INDEX:
                                Next();
                                return CreateIndex();
                            case Sym.PRIMARY:
                                Next(); Mustbe(Sym.INDEX);
                                return CreateIndex(true);
                        }
                        throw new Exception("Unknown Create " + lxr.tok);
                    }
                case Sym.DROP:
                    return Drop();
                case Sym.INSERT:
                    return Insert();
                case Sym.DELETE:
                    return Delete();
                case Sym.UPDATE:
                    return Update();
                case Sym.SELECT:
                    return Select();
                case Sym.BEGIN:
                    return new Serialisable(Types.SBegin);
                case Sym.ROLLBACK:
                    return new Serialisable(Types.SRollback);
                case Sym.COMMIT:
                    return new Serialisable(Types.SCommit);
            }
            throw new Exception("Syntax Error: " + lxr.tok);
        }
        /// <summary>
        /// Alter: ALTER table_id ADD id Type
	    /// | ALTER table_id DROP col_id 
        /// | ALTER table_id[COLUMN col_id] TO id[Type] .
        /// </summary>
        /// <returns></returns>
        Serialisable Alter()
        {
            Next();
            var tb = MustBeID();
            SString? col = null;
            var add = false;
            switch (lxr.tok)
            {
                case Sym.COLUMN:
                    Next();
                    col = MustBeID();
                    Mustbe(Sym.TO);
                    break;
                case Sym.DROP:
                    Next();
                    col = MustBeID();
                    return new SDropStatement(col.str, tb.str); // ok
                case Sym.TO:
                    Next(); break;
                case Sym.ADD:
                    Next(); add = true;
                    break;
            }
            var nm = MustBeID();
            Types dt = Types.Serialisable;
            switch (lxr.tok)
            {
                case Sym.TIMESTAMP: Next(); dt = Types.STimestamp; break;
                case Sym.INTEGER: Next(); dt = Types.SInteger; break;
                case Sym.NUMERIC: Next(); dt = Types.SNumeric; break;
                case Sym.STRING: Next(); dt = Types.SString; break;
                case Sym.DATE: Next(); dt = Types.SDate; break;
                case Sym.TIMESPAN: Next(); dt = Types.STimeSpan; break;
                case Sym.BOOLEAN: Next(); dt = Types.SBoolean; break;
                default: if (add)
                        throw new Exception("Type expected");
                    break;
            }
            if (col == null)
                throw new System.Exception("??");
            return new SAlterStatement(tb.str, col.str, nm.str, dt); // ok
        }
        Serialisable CreateTable()
        {
            Next();
            var id = MustBeID();
            var tb = id.str; // ok
            Mustbe(Sym.LPAREN);
            var cols = SList<SColumn>.Empty;
            for (; ; )
            {
                var c = MustBeID();
                var t = For(lxr.tok);
                cols = cols.InsertAt(new SColumn(c.str, t), cols.Length.Value); // ok
                Next();
                switch(lxr.tok)
                {
                    case Sym.RPAREN: Next(); return new SCreateTable(tb, cols);
                    case Sym.COMMA: Next(); continue;
                }
                throw new Exception("Syntax error: " + lxr.tok);
            }
        }
        SList<SSelector> Cols()
        {
            var cols = SList<SSelector>.Empty;
            Mustbe(Sym.LPAREN);
            for (; ;)
            {
                var c = MustBeID();
                cols = cols.InsertAt(new SColumn(c.str), cols.Length.Value); // ok
                switch (lxr.tok)
                {
                    case Sym.RPAREN: Next(); return cols;
                    case Sym.COMMA: Next(); continue;
                }
                throw new Exception("Syntax error: " + lxr.tok);
            }
        }
        SValues Vals()
        {
            Next();
            var cols = SList<Serialisable>.Empty;
            Mustbe(Sym.LPAREN);
            for (; ; )
            {
                cols = cols.InsertAt(Value(), cols.Length.Value);
                switch (lxr.tok)
                {
                    case Sym.RPAREN: Next(); return new SValues(cols);
                    case Sym.COMMA: Next(); continue;
                }
                throw new Exception("Syntax error: " + lxr.tok);
            }
        }
        SList<SSlot<string,Serialisable>> Selects()
        {
            var r = SList<SSlot<string, Serialisable>>.Empty;
            var k = 0;
            for (; ;Next())
            {
                var n = "col" + (k+1);
                var c = Value();
                if (c is SSelector sc)
                    n = sc.name;
                if (c is SExpression se && se.op == SExpression.Op.Dot)
                    n = ((SString)se.left).str + "." + ((SString)se.right).str;
                if (lxr.tok == Sym.AS)
                {
                    Next();
                    var tv = MustBeID();
                    n = ((SString)tv).str;
                }
                r = r.InsertAt(new SSlot<string, Serialisable>(n, c??Serialisable.Null), k++);
                if (lxr.tok != Sym.COMMA)
                    return r;
            }
        }
        Serialisable CreateIndex(bool primary=false)
        {
            var id = MustBeID();
            Mustbe(Sym.FOR);
            var tb = MustBeID();
            var cols = Cols();
            Serialisable rt = Serialisable.Null;
            if (lxr.tok==Sym.REFERENCES)
            {
                Next();
                rt = MustBeID();
            }
            return new SCreateIndex(id, tb, SBoolean.For(primary), rt, cols); // ok
        }
        Serialisable Drop() // also see Drop column in Alter
        {
            Next();
            var id = MustBeID();
            return new SDropStatement(id.str, "");
        }
        Serialisable Insert()
        {
            Next();
            var id = MustBeID();
            var cols = SList<SSelector>.Empty;
            if (lxr.tok == Sym.LPAREN)
                cols = Cols();
            Serialisable vals;
            if (lxr.tok == Sym.VALUES)
                vals = Vals();
            else
            {
                Mustbe(Sym.SELECT);
                vals = Select();
            }
            return new SInsertStatement(id.str, cols, vals);
        }
        SQuery Query()
        {
            var id = MustBeID();
            var tb = new STable(id.str);
            Serialisable alias = Serialisable.Null;
            if (lxr.tok==Sym.ID && lxr.val!=null)
            {
                alias = lxr.val;
                Next();
            }
            var wh = SList<Serialisable>.Empty;
            var tt = Sym.WHERE;
            for (; lxr.tok==tt;)
            {
                Next(); tt = Sym.AND;
                wh = wh.InsertAt(Value(),wh.Length.Value);
            }
            if (wh.Length == 0 && alias==Serialisable.Null) return tb;
            return new SSearch(tb, alias, wh);
        }
        Serialisable Value()
        {
            var a = Conjunct();
            while (lxr.tok==Sym.AND)
            {
                Next();
                a = new SExpression(a, SExpression.Op.And, Conjunct());
            }
            return a;
        }
        Serialisable Conjunct()
        {
            var a = Item();
            while (lxr.tok==Sym.OR)
            {
                Next();
                a = new SExpression(a, SExpression.Op.Or, Item());
            }
            return a;
        }
        Serialisable Item()
        {
            var a = OneVal();
            if (lxr.tok >= Sym.EQUAL && lxr.tok <= Sym.GTR)
            {
                var op = SExpression.Op.Eql;
                switch(lxr.tok)
                {
                    case Sym.NEQ: op = SExpression.Op.NotEql; break;
                    case Sym.LEQ: op = SExpression.Op.Leq; break;
                    case Sym.LSS: op = SExpression.Op.Lss; break;
                    case Sym.GEQ: op = SExpression.Op.Geq; break;
                    case Sym.GTR: op = SExpression.Op.Gtr; break;
                    case Sym.AND: op = SExpression.Op.And; break;
                    case Sym.OR: op = SExpression.Op.Or; break;
                }
                Next();
                a = new SExpression(a, op, OneVal());
            }
            return a;
        }
        Serialisable OneVal()
        {
            Serialisable a = Term();
            while (lxr.tok==Sym.PLUS || lxr.tok==Sym.MINUS)
            {
                SExpression.Op op = SExpression.Op.Or;
                switch (lxr.tok)
                {
                    case Sym.PLUS: op = SExpression.Op.Plus; break;
                    case Sym.MINUS: op = SExpression.Op.Minus; break;
                }
                Next();
                a = new SExpression(a, op, Term());
            }
            return a;
        }
        Serialisable Term()
        {
            if (lxr.tok == Sym.MINUS || lxr.tok == Sym.PLUS || lxr.tok == Sym.NOT)
            {
                var op = SExpression.Op.Plus;
                switch (lxr.tok)
                {
                    case Sym.MINUS:
                        op = SExpression.Op.UMinus; break;
                    case Sym.NOT:
                        op = SExpression.Op.Not; break;
                    case Sym.PLUS:
                        Next();
                        return Term();
                }
                Next();
                return new SExpression(Term(), op, Serialisable.Null);
            }
            var a = Factor();
            while (lxr.tok == Sym.TIMES || lxr.tok == Sym.DIVIDE)
            {
                SExpression.Op op = SExpression.Op.And;
                switch (lxr.tok)
                {
                    case Sym.TIMES: op = SExpression.Op.Times; break;
                    case Sym.DIVIDE: op = SExpression.Op.Divide; break;
                }
                Next();
                a = new SExpression(a, op, Factor());
            }
            if (lxr.tok==Sym.IS)
            {
                Next();
                Mustbe(Sym.NULL);
                return new SExpression(a, SExpression.Op.Eql, Serialisable.Null);
            }
            if (lxr.tok==Sym.IN)
            {
                Next();
                return new SInPredicate(a, Value());
            }
            return a;
        }
        Serialisable Factor()
        {
            var v = lxr.val;
            switch (lxr.tok)
            {
                case Sym.LITERAL:
                    Next();
                    return v ?? throw new Exception("??");
                case Sym.ID:
                    {
                        if (v == null)
                            throw new Exception("??");
                        var s = ((SString)v).str;
                        Next();
                        if (lxr.tok == Sym.DOT)
                        {
                            Next();
                            var nv = MustBeID();
                            return new SExpression(v, SExpression.Op.Dot, nv);
                        }
                        return new SColumn(((SString)v).str);
                    }
                case Sym.SUM:
                case Sym.COUNT:
                case Sym.MAX:
                case Sym.MIN:
                    {
                        var t = lxr.tok;
                        Next(); Mustbe(Sym.LPAREN);
                        var a = Value();
                        Mustbe(Sym.RPAREN);
                        return Call(t, a);
                    }
                case Sym.LPAREN:
                    {
                        Next();
                        var a = SList<Serialisable>.Empty;
                        int n = 0;
                        if (lxr.tok!=Sym.RPAREN)
                        {
                            a = a.InsertAt(Value(), n++);
                            while (lxr.tok==Sym.COMMA)
                            {
                                Next();
                                a = a.InsertAt(Value(), n++);
                            }
                        }
                        Mustbe(Sym.RPAREN);
                        return new SRow(a);
                    }
                case Sym.SELECT:
                    return Select();
            }
            throw new Exception("Bad syntax");
        }
        Serialisable Call(Sym s,Serialisable a)
        {
            var f = SFunction.Func.Sum;
            switch (s)
            {
                case Sym.COUNT: f = SFunction.Func.Count; break;
                case Sym.MAX: f = SFunction.Func.Max; break;
                case Sym.MIN: f = SFunction.Func.Min; break;
            }
            return new SFunction(f, a);
        }
        Serialisable Select()
        {
            Next();
            var dct = false;
            if (lxr.tok == Sym.DISTINCT)
            {
                dct = true;
                Next();
            }
            var sels = SList<SSlot<string, Serialisable>>.Empty;
            if (lxr.tok!=Sym.FROM)
                sels = Selects();
            var als = SDict<int, string>.Empty;
            var cp = SDict<int, Serialisable>.Empty;
            var k = 0;
            for (var b = sels.First();b!=null;b=b.Next())
            {
                als = als + (k, b.Value.key);
                cp = cp + (k++, b.Value.val);
            }
            Mustbe(Sym.FROM);
            var q = Query();
            var or = SList<SOrder>.Empty;
            var i = 0;
            if (lxr.tok == Sym.ORDERBY)
            {
                Next();
     //           Mustbe(Sym.BY);
                for (; ; )
                {
                    var c = Value();
                    var d = false;
                    if (lxr.tok == Sym.DESC)
                    {
                        Next();
                        d = true;
                    }
                    or = or.InsertAt(new SOrder(c, d), i++);
                    if (lxr.tok == Sym.COMMA)
                        Next();
                    else
                        break;
                }
            }
            return new SSelectStatement(dct, als, cp, q, or);
        }
        Serialisable Delete()
        {
            Next();
            return new SDeleteSearch(Query());
        }
        Serialisable Update()
        {
            Next();
            var q = Query();
            var sa = SDict<string, Serialisable>.Empty;
            Mustbe(Sym.SET);
            var tt = Sym.SET;
            for (; lxr.tok == tt;)
            {
                Next(); tt = Sym.COMMA;
                var c = MustBeID();
                Mustbe(Sym.EQUAL);
                sa = sa + (c.str, Value());
            }
            return new SUpdateSearch(q, sa);
        }
    }
}
