using System;
using System.Collections.Generic;
using System.Text;
using Pyrrho.Level3;
using Pyrrho.Common;
// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2019
//
// This software is without support and no liability for damage consequential to use
// You can view and test this code 
// All other use or distribution or the construction of any product incorporating this technology 
// requires a license from the University of the West of Scotland
namespace Pyrrho.Level4
{

    /// <summary>
    /// Implement an identifier lexeme class.
    /// Ident is not immutable and must not be used in any property of any subclass of Basis.
    /// </summary>
    internal class Ident : IComparable
    {
        public readonly Ident sub = null;
        bool indexed = false;
        internal readonly long iix;
        string _ident;
        internal string ident
        {
            get => _ident;
            set
            {
                if (indexed)
                    throw new PEException("PE725");
                _ident = value;
            }
        }
        long _segpos = 0;
        internal long segpos
        {
            get => _segpos;
            set
            {
                //      if (segindexed)
                //         throw new PEException("PE726");
                _segpos = value;
            }
        }
        /// <summary>
        /// the start position in the lexer
        /// </summary>
        internal readonly int lxrstt = 0;
        /// <summary>
        /// the current position in the lexer
        /// </summary>
        internal int lxrpos = 0;
        /// <summary>
        /// The lexer responsible
        /// </summary>
        internal Lexer lexer = null;
        /// <summary>
        /// position in list of requested columns
        /// </summary>
        internal int reqpos = -1;
        internal Ident(Ident n, long sp)
        {
            iix = n.iix;
            ident = n.ident;
            sub = n.sub;
            _segpos = sp;
        }
        internal Ident(Lexer lx, string s = null)
        {
            ident = s ?? ((lx.tok == Sqlx.ID) ? lx.val.ToString() : lx.tok.ToString());
            lexer = lx;
            lxrstt = lx.start;
            iix = lx.Position;
            lxrpos = lx.pos;
            if (lx.tok == Sqlx.ID)
            {
                lx.idents += (lx.Position, this);
                if (lx.lookup.Contains(this))
                    segpos = lx.lookup[this];
                else
                {
                    segpos = lx.Position;
                    lx.lookup += (this, segpos);
                }
            }
        }
        internal Ident(Lexer lx, int st, int pos, long dp, Ident sb = null)
        {
            iix = lx.Position;
            ident = new string(lx.input, st, pos - st);
            lexer = lx;
            lxrstt = st;
            lxrpos = pos;
            segpos = dp;
            sub = sb;
        }
        internal Ident(Ident lf, Ident sb)
        {
            ident = lf.ident;
            iix = lf.iix + ident.Length;
            reqpos = lf.reqpos;
            lexer = lf.lexer;
            lxrstt = lf.lxrstt;
            lxrpos = lf.lxrpos;
            long sg = 0;
            if (ident.Length > 0)
            {
                //          if (ident[0] == '\'' && long.TryParse(ident.Substring(1), out sg)) // this is very unlikely
                //              sg  += Transaction.TransPos;
                //          else
                long.TryParse(ident, out sg);
            }
            segpos = (sg > 0) ? sg : lf.segpos;
            //      if (_segpos > 0 && Renamable() && lexer!=null)
            //          lexer.ctx.refs +=(this, segpos);
            sub = sb;
        }
        internal Ident(string s, long dp)
        {
            iix = dp;
            ident = s; _segpos = dp;
        }
        internal int Length()
        {
            return 1 + (sub?.Length() ?? 0);
        }
        public void Set(long dp)
        {
            if (dp == 0)
                return;
            var fi = Final();
            if (fi.segpos != 0)
                return;
            fi.segpos = dp;
        }
        internal Ident Final()
        {
            return sub?.Final() ?? this;
        }
        public Ident Target()
        {
            if (sub == null)
                return null;
            return new Ident(lexer, lxrstt, lxrpos, segpos, sub.Target()) { ident = ident};
        }
        public bool HeadsMatch(Ident a)
        {
            if (a == null)
                return false;
            if (segpos != 0 && segpos == a.segpos)
                return true;
            if (((segpos == 0 || segpos == a.segpos) && ident == a.ident))
                return true;
            return false;
        }
        public bool _Match(Ident a)
        {
            return HeadsMatch(a) && ((sub == null && a.sub == null) || sub?._Match(a.sub) == true);
        }
        public Ident Suffix(Ident pre)
        {
            if (pre == null)
                return this;
            if (!HeadsMatch(pre) || sub == null)
                return null;
            return sub.Suffix(pre.sub);
        }
        public long Defpos()
        {
            return iix;
        }
        /// <summary>
        /// Instead of this, use a new Ident that has $arity appended if necessary.
        /// Check Metdata $seq usage. XXX
        /// </summary>
        /// <returns></returns>
        internal Ident Suffix(int x)
        {
            if (segpos > 0 || ident.Contains("$"))
                return this;
            if (ident != "" && Char.IsDigit(ident[0]))
            {
                segpos = long.Parse(ident);
                return this;
            }
            return new Ident(ident + "$" + x, 0);
        }
        /// <summary>
        /// RowTypes should not have dots
        /// </summary>
        /// <returns>The dot-less version of the Ident</returns>
        internal Ident ForTableType()
        {
            return (sub is Ident id) ? id : this;
        }
        public override string ToString()
        {
            if (ident == "...") // special case for anonymous row types: use NameInSession for readable version
                return "(...)";
            var sb = new StringBuilder();
            if (ident != null)
                sb.Append(ident);
            else if (segpos > 0)
                sb.Append("\"" + segpos + "\"");
            else
                sb.Append("??");
            if (sub != null)
            {
                sb.Append(".");
                sb.Append(sub.ToString());
            }
            return sb.ToString();
        }
        internal void ToString1(StringBuilder sb, Context cx, string eflag)
        {
            sb.Append(ident);
            if (segpos > 0 && eflag != null)
            {
                sb.Append(' ');
                sb.Append(segpos);
            }
            if (sub != null)
            {
                sb.Append('.');
                sub.ToString1(sb, cx, eflag);
            }
        }
        /// <summary>
        /// When a Domain is placed in the PhysBase we need to remove context information from names
        /// </summary>
        internal void Clean()
        {
            lexer = null;
            lxrpos = 0;
            reqpos = 0;
        }

        public int CompareTo(object obj)
        {
            if (obj == null)
                return 1;
            var that = (Ident)obj;
            var c = ident.CompareTo(that.ident);
            if (c != 0)
                return c;
            return 0;
        }
        /// <summary>
        /// Wraps a structure for managing Idents distinguished by segpos or ident string
        /// </summary>
        /// <typeparam name="V"></typeparam>
        internal class Tree<V>
        {
            BTree<long, BTree<Ident, V>> bTree;
            BTree<Ident, V> idTree;
            Tree(BTree<long, BTree<Ident, V>> bT, BTree<Ident, V> idT) { bTree = bT; idTree = idT; }
            public Tree(Ident k, V v)
                : this(new BTree<long, BTree<Ident, V>>(k.Defpos(), new BTree<Ident, V>(k, v)),
                     (k.ident != null && k.ident != "") ? new IdTree<V>(k, v) : IdTree<V>.Empty)
            { }

            public static readonly Tree<V> Empty = new Tree<V>(BTree<long, BTree<Ident, V>>.Empty, IdTree<V>.Empty);
            public static void Add(ref Tree<V> t, Ident k, V v)
            {
                var bT = t.bTree;
                var idT = t.idTree;
                if (k.Defpos() != 0)
                {
                    var tb = bT[k.Defpos()];
                    if (tb == null)
                        tb = new BTree<Ident, V>(k, v);
                    else
                        tb +=(k, v);
                    bT +=(k.Defpos(), tb);
                }
                if (k.ident != null && k.ident != "")
                {
                    if (idT.Contains(k))
                    {
                        var b = idT.PositionAt(k);
                        if (b.key().Defpos() == 0 && k.Defpos() > 0)
                            idT -=k;
                    }
                    idT +=(k, v);
                }
                t = new Tree<V>(bT, idT);
            }
            public static Tree<V> operator +(Tree<V> t, (Ident, V) v)
            {
                Add(ref t, v.Item1, v.Item2);
                return t;
            }
            public static Tree<V> operator -(Tree<V> t, Ident id)
            {
                Remove(ref t, id);
                return t;
            }
            public static void AddNN(ref Tree<V> tree, Ident k, V v)
            {
                if (v == null)
                    throw new Exception("PE000");
                Add(ref tree, k, v);
            }
            public static void Update(ref Tree<V> t, Ident k, V v)
            {
                var bT = t.bTree;
                var idT = t.idTree;
                if (bT[k.Defpos()] is BTree<Ident, V> tb)
                {
                    tb +=(k, v);
                    bT +=(k.Defpos(), tb);
                }
                if (idT.Contains(k))
                    idT +=(k, v);
                t = new Tree<V>(bT, idT);
            }
            public static void Remove(ref Tree<V> t, Ident k)
            {
                var bT = t.bTree;
                var idT = t.idTree;
                bool done = false;
                if (bT[k.Defpos()] is BTree<Ident, V> tb)
                {
                    tb -= k;
                    if (tb == null || tb.Count == 0)
                        bT -= k.Defpos();
                    else
                        bT +=(k.Defpos(), tb);
                    done = true;
                }
                if (idT.Contains(k))
                {
                    idT -= k;
                    done = true;
                }
                if (done)
                    t = (idT.Count == 0) ? Empty : new Tree<V>(bT, idT);
            }
            public int Count { get { return (int)idTree.Count; } }
            public bool Contains(Ident k)
            {
                return bContains(k) || idTree.Contains(k);
            }
            public bool bContains(Ident k)
            {
                return (bTree[k.Defpos()] is BTree<Ident, V> tb) ? tb.Contains(k) : false;
            }
            public V this[Ident k]
            {
                get
                {
                    if (k == null)
                        return default(V);
                    return (bTree.Contains(k.segpos)) ? bTree[k.segpos].First().value() : idTree[k];
                }
            }
            public Bookmark First()
            {
                return Bookmark.New(this);
            }

            internal Tree<V> Add(Tree<V> tree)
            {
                var r = this;
                for (var b = tree.First(); b != null; b = b.Next())
                    Add(ref r, b.key(), b.value());
                return r;
            }

            internal class Bookmark
            {
                readonly Tree<V> _t;
                readonly ABookmark<long, BTree<Ident, V>> _bB;
                readonly ABookmark<Ident, V> _idB;
                Bookmark(Tree<V> t, ABookmark<long, BTree<Ident, V>> b, ABookmark<Ident, V> id)
                {
                    _t = t; _bB = b; _idB = id;
                }
                internal static Bookmark New(Tree<V> t)
                {
                    if (t.bTree.Count == 0 && t.idTree.Count == 0)
                        return null;
                    return (t.bTree.Count > t.idTree.Count) ? new Bookmark(t, t.bTree.First(), null) : new Bookmark(t, null, t.idTree.First());
                }
                internal Bookmark Next()
                {
                    if (_bB != null) return (_bB.Next() is ABookmark<long, BTree<Ident, V>> b) ? new Bookmark(_t, b, null) : null;
                    return (_idB.Next() is ABookmark<Ident, V> c) ? new Bookmark(_t, null, c) : null;
                }
                public Ident key() { return _bB?.value()?.First().key() ?? _idB.key(); }
                public V value() { return (_bB?.value() is BTree<Ident, V> tb) ? tb.First().value() : _idB.value(); }
            }
        }
        /// <summary>
        /// Define an ordering based on identifier, dbix, segpos
        /// </summary>
        /// <typeparam name="V"></typeparam>
        internal class PosTree<V> : BTree<Ident, V>
        {
            public readonly new static PosTree<V> Empty = new PosTree<V>();
            public PosTree() : base(null) { }
            public PosTree(Ident k, V v) : base(new Leaf<Ident, V>(new KeyValuePair<Ident, V>(k, v))) { }
            protected PosTree(Bucket<Ident, V> b) : base(b) { }
            public override int Compare(Ident a, Ident b)
            {
                int c = a.ident.CompareTo(b.ident);
                if (c != 0)
                    return c;
                return a.segpos.CompareTo(b.segpos);
            }
            public static void Add(ref PosTree<V> t, Ident k, V v)
            {
                t = (PosTree<V>)t.Add(k, v);
            }
            public static PosTree<V> operator +(PosTree<V> t, (Ident, V) v)
            {
                return (PosTree<V>)t.Add(v.Item1, v.Item2);
            }
            public static void Remove(ref PosTree<V> t, Ident k)
            {
                t.Remove(k);
            }
            protected override ATree<Ident, V> Add(Ident k, V v)
            {
                if (Contains(k))
                    return new PosTree<V>(root.Update(this, k, v));
                return Insert(k, v);
            }

            protected override ATree<Ident, V> Insert(Ident k, V v)
            {
                if (root == null || root.total == 0)  // empty BTree
                    return new PosTree<V>(k, v);
                if (root.count == Size)
                    return new PosTree<V>(root.Split()).Add(k, v);
                return new PosTree<V>(root.Add(this, k, v));
            }

            protected override ATree<Ident, V> Remove(Ident k)
            {
                if (k == null)
                    return this;
                k.indexed = false;
                if (!Contains(k))
                    return this;
                if (root.total == 1) // empty index
                    return Empty;
                // note: we allow root to have 1 entry
                return new PosTree<V>(root.Remove(this, k));
            }

            protected override ATree<Ident, V> Update(Ident k, V v)
            {
                if (!Contains(k))
                    throw new Exception("PE01");
                return new PosTree<V>(root.Update(this, k, v));
            }
        }
        /// <summary>
        /// Define a tree using an ordering based on the identifier chain
        /// </summary>
        /// <typeparam name="V"></typeparam>
        internal class IdTree<V> : BTree<Ident, V>
        {
            public new readonly static IdTree<V> Empty = new IdTree<V>();
            public IdTree() : base(null) { }
            public IdTree(Ident k, V v) : base(new Leaf<Ident, V>(new KeyValuePair<Ident, V>(k, v))) { }
            protected IdTree(Bucket<Ident, V> b) : base(b) { }
            public override int Compare(Ident a, Ident b)
            {
                int c = a.ident.CompareTo(b.ident);
                if (c != 0)
                    return c;
                if (a.sub == null)
                    return (b.sub == null) ? 0 : -1;
                if (b.sub == null)
                    return 1;
                return Compare(a.sub, b.sub);
            }
            public static void Add(ref IdTree<V> t, Ident k, V v)
            {
                t = (IdTree<V>)t.Add(k, v);
            }
            public static IdTree<V> operator +(IdTree<V> t, (Ident, V) v)
            {
                return (IdTree<V>)t.Add(v.Item1, v.Item2);
            }
            protected override ATree<Ident, V> Add(Ident k, V v)
            {
                if (Contains(k))
                    return new IdTree<V>(root.Update(this, k, v));
                return Insert(k, v);
            }

            protected override ATree<Ident, V> Insert(Ident k, V v)
            {
                k.indexed = true;
                if (root == null || root.total == 0)  // empty BTree
                    return new IdTree<V>(k, v);
                if (root.count == Size)
                    return new IdTree<V>(root.Split()).Add(k, v);
                return new IdTree<V>(root.Add(this, k, v));
            }

            protected override ATree<Ident, V> Remove(Ident k)
            {
                if (k == null)
                    return this;
                k.indexed = false;
                if (!Contains(k))
                    return this;
                if (root.total == 1) // empty index
                    return Empty;
                // note: we allow root to have 1 entry
                return new IdTree<V>(root.Remove(this, k));
            }
            public static void Remove(ref IdTree<V> t, Ident k)
            {
                t.Remove(k);
            }
            protected override ATree<Ident, V> Update(Ident k, V v)
            {
                if (!Contains(k))
                    throw new Exception("PE01");
                return new IdTree<V>(root.Update(this, k, v));
            }
            public static IdTree<V> operator -(IdTree<V>t,Ident v)
            {
                return (IdTree<V>)t.Remove(v);
            }
        }
    }
    /// <summary>
    /// Lexical analysis for SQL
    /// </summary>
    internal class Lexer
	{
        /// <summary>
        /// The entire input string
        /// </summary>
		public char[] input;
        /// <summary>
        /// The current position (just after tok) in the input string
        /// </summary>
		public int pos,pushPos;
        /// <summary>
        /// The start of tok in the input string
        /// </summary>
		public int start = 0, pushStart; 
        /// <summary>
        /// the current character in the input string
        /// </summary>
		char ch,pushCh;
        /// <summary>
        /// The current token's identifier
        /// </summary>
		public Sqlx tok;
        public Sqlx pushBack = Sqlx.Null;
        public Query cur = null;
        internal BTree<long, Ident> idents = BTree<long, Ident>.Empty;
        internal BTree<Ident, long> lookup = BTree<Ident, long>.Empty;
        public long offset;
        public long Position => offset + start;
        /// <summary>
        /// The current token's value
        /// </summary>
		public TypedValue val = null;
        public TypedValue pushVal;
        /// <summary>
        /// Entries in the reserved word table
        /// If there are more than 2048 reserved words, the hp will hang
        /// </summary>
		class ResWd
		{
			public Sqlx typ;
			public string spell;
			public ResWd(Sqlx t,string s) { typ=t; spell=s; }
		}
 		static ResWd[] resWds = new ResWd[0x800]; // open hash
        static Lexer()
        {
            int h;
            for (Sqlx t = Sqlx.ABS; t <= Sqlx.YEAR; t++)
                if (t != Sqlx.TYPE) // TYPE is not a reserved word but is in this range
                {
                    string s = t.ToString();
                    h = s.GetHashCode() & 0x7ff;
                    while (resWds[h] != null)
                        h = (h + 1) & 0x7ff;
                    resWds[h] = new ResWd(t, s);
                }
            // while XML is a reserved word and is not in the above range
            h = "XML".GetHashCode() & 0x7ff; 
            while (resWds[h] != null)
                h = (h + 1) & 0x7ff;
            resWds[h] = new ResWd(Sqlx.XML, "XML");
        }
        /// <summary>
        /// Check if a string matches a reserved word.
        /// tok is set if it is a reserved word.
        /// </summary>
        /// <param name="s">The given string</param>
        /// <returns>true if it is a reserved word</returns>
		internal bool CheckResWd(string s)
		{
			int h = s.GetHashCode() & 0x7ff;
			for(;;)
			{
				ResWd r = resWds[h];
				if (r==null)
					return false;
				if (r.spell==s)
				{
					tok = r.typ;
					return true;
				}
				h = (h+1)&0x7ff;
			}
		}
        internal object Diag { get { if (val == TNull.Value) return tok; return val; } }
       /// <summary>
        /// Constructor: Start a new lexer
        /// </summary>
        /// <param name="s">the input string</param>
        internal Lexer(string s,long off = 0)
        {
   		    input = s.ToCharArray();
			pos = -1;
            offset = off;
			Advance();
			tok = Next();
        }
        /// <summary>
        /// Mutator: Advance one position in the input
        /// ch is set to the new character
        /// </summary>
        /// <returns>The new value of ch</returns>
		public char Advance()
		{
			if (pos>=input.Length)
				throw new DBException("42150").Mix();
			if (++pos>=input.Length)
				ch = (char)0;
			else
				ch = input[pos];
			return ch;
		}
        /// <summary>
        /// Decode a hexadecimal digit
        /// </summary>
        /// <param name="c">[0-9a-fA-F]</param>
        /// <returns>0..15</returns>
		internal static int Hexit(char c)
		{
			switch (c)
			{
				case '0': return 0;
				case '1': return 1;
				case '2': return 2;
				case '3': return 3;
				case '4': return 4;
				case '5': return 5;
				case '6': return 6;
				case '7': return 7;
				case '8': return 8;
				case '9': return 9;
				case 'a': return 10;
				case 'b': return 11;
				case 'c': return 12;
				case 'd': return 13;
				case 'e': return 14;
				case 'f': return 15;
				case 'A': return 10;
				case 'B': return 11;
				case 'C': return 12;
				case 'D': return 13;
				case 'E': return 14;
				case 'F': return 15;
				default: return -1;
			}
		}
        public Sqlx PushBack(Sqlx old)
        {
            pushBack = tok;
            pushVal = val;
            pushStart = start;
            pushPos = pos;
            pushCh = ch;
            tok = old;
            return tok;
        }
        public Sqlx PushBack(Sqlx old,TypedValue oldVal)
        {
            val = oldVal;
            return PushBack(old);
        }
        /// <summary>
        /// Advance to the next token in the input.
        /// tok and val are set for the new token
        /// </summary>
        /// <returns>The new value of tok</returns>
		public Sqlx Next()
		{
            if (pushBack != Sqlx.Null)
            {
                tok = pushBack;
                val = pushVal;
                start = pushStart;
                pos = pushPos;
                ch = pushCh;
                pushBack = Sqlx.Null;
                return tok;
            }
            val = TNull.Value;
			while (char.IsWhiteSpace(ch))
				Advance();
			start = pos;
			if (char.IsLetter(ch))
			{
				char c = ch;
				Advance();
				if (c=='X' && ch=='\'')
				{
					int n = 0;
					if (Hexit(Advance())>=0)
						n++;
					while (ch!='\'')
						if (Hexit(Advance())>=0)
							n++;
					n = n/2;
					byte[] b = new byte[n];
					int end = pos;
					pos = start+1;
					for (int j=0;j<n;j++)
					{
						while (Hexit(Advance())<0)
							;
						int d = Hexit(ch)<<4;
						d += Hexit(Advance());
						b[j] = (byte)d;
					}
					while (pos!=end)
						Advance();
					tok = Sqlx.BLOBLITERAL;
					val = new TBlob(b);
					Advance();
					return tok;
				}
				while (char.IsLetterOrDigit(ch) || ch=='_')
					Advance();
				string s0 = new string(input,start,pos-start);
                string s = s0.ToUpper();
				if (CheckResWd(s))
				{
					switch(tok)
					{
						case Sqlx.TRUE: val = TBool.True; return Sqlx.BOOLEANLITERAL;
						case Sqlx.FALSE: val = TBool.False; return Sqlx.BOOLEANLITERAL;
						case Sqlx.UNKNOWN: val = null; return Sqlx.BOOLEANLITERAL;
                        case Sqlx.CURRENT_DATE: val = new TDateTime(DateTime.Today); return tok;
                        case Sqlx.CURRENT_TIME: val = new TTimeSpan(DateTime.Now - DateTime.Today); return tok;
                        case Sqlx.CURRENT_TIMESTAMP: val = new TDateTime(DateTime.Now); return tok;
					}
					return tok;
				}
				val = new TChar(s);
				return tok=Sqlx.ID;
			}
			string str;
			if (char.IsDigit(ch))
			{
				start = pos;
				while (char.IsDigit(Advance()))
					;
				if (ch!='.')
				{
					str = new string(input,start,pos-start);
					if (pos-start>18)
						val = new TInteger(Integer.Parse(str));
					else
						val = new TInt(long.Parse(str));
					tok=Sqlx.INTEGERLITERAL;
					return tok;
				}
				while (char.IsDigit(Advance()))
					;
				if (ch!='e' && ch!='E')
				{
					str = new string(input,start,pos-start);
					val = new TNumeric(Common.Numeric.Parse(str));
					tok=Sqlx.NUMERICLITERAL;
					return tok;
				}
				if (Advance()=='-'||ch=='+')
					Advance();
				if (!char.IsDigit(ch))
					throw new DBException("22107").Mix();
				while (char.IsDigit(Advance()))
					;
				str = new string(input,start,pos-start);
				val = new TReal(Common.Numeric.Parse(str));
				tok=Sqlx.REALLITERAL;
				return tok;
			}
			switch (ch)
			{
				case '[':	Advance(); return tok=Sqlx.LBRACK;
				case ']':	Advance(); return tok=Sqlx.RBRACK;
				case '(':	Advance(); return tok=Sqlx.LPAREN;
				case ')':	Advance(); return tok=Sqlx.RPAREN;
				case '{':	Advance(); return tok=Sqlx.LBRACE;
				case '}':	Advance(); return tok=Sqlx.RBRACE;
				case '+':	Advance(); return tok=Sqlx.PLUS;
				case '*':	Advance(); return tok=Sqlx.TIMES;
				case '/':	Advance(); return tok=Sqlx.DIVIDE;
				case ',':	Advance(); return tok=Sqlx.COMMA;
				case '.':	Advance(); return tok=Sqlx.DOT;
				case ';':	Advance(); return tok=Sqlx.SEMICOLON;
/* from v5.5 Document syntax allows exposed SQL expressions
                case '{':
                    {
                        var braces = 1;
                        var quote = '\0';
                        while (pos<input.Length)
                        {
                            Advance();
                            if (ch == '\\')
                            {
                                Advance();
                                continue;
                            }
                            else if (ch == quote)
                                quote = '\0';
                            else if (quote == '\0')
                            {
                                if (ch == '{')
                                    braces++;
                                else if (ch == '}' && --braces == 0)
                                {
                                    Advance();
                                    val = new TDocument(ctx,new string(input, st, pos - st));
                                    return tok = Sqlx.DOCUMENTLITERAL;
                                }
                                else if (ch == '\'' || ch == '"')
                                    quote = ch;
                            }
                        }
                        throw new DBException("42150",new string(input,st,pos-st));
                    } */
				case ':':	Advance(); 
					if (ch==':')
					{
						Advance();
						return tok=Sqlx.DOUBLECOLON;
					}
					return tok=Sqlx.COLON;
				case '-':	
					if (Advance()=='-')
					{
						Advance();    // -- comment
						while (pos<input.Length) 
							Advance();
						return Next();
					}
					return tok=Sqlx.MINUS;
				case '|':	
					if (Advance()=='|')
					{
						Advance();
						return tok=Sqlx.CONCATENATE;
					}
					return tok=Sqlx.VBAR;
				case '<' : 
					if (Advance()=='=')
					{
						Advance();
						return tok=Sqlx.LEQ; 
					}
					else if (ch=='>')
					{
						Advance();
						return tok=Sqlx.NEQ;
					}
					return tok=Sqlx.LSS;
				case '=':	Advance(); return tok=Sqlx.EQL;
				case '>':
					if (Advance()=='=')
					{
						Advance();
						return tok=Sqlx.GEQ;
					}
					return tok=Sqlx.GTR;
				case '"':	// delimited identifier
				{
					start = pos;
					while (Advance()!='"')
						;
					val = new TChar(new string(input,start+1,pos-start-1));
                    Advance();
                    while (ch == '"')
                    {
                        var fq = pos;
                        while (Advance() != '"')
                            ;
                        val = new TChar(val.ToString()+new string(input, fq, pos - fq));
                        Advance();
                    }
					tok=Sqlx.ID;
         //           CheckForRdfLiteral();
                    return tok;
				}
				case '\'': 
				{
					start = pos;
					var qs = new Stack<int>();
                    qs.Push(-1);
					int qn = 0;
					for (;;)
					{
						while (Advance()!='\'')
							;
						if (Advance()!='\'')
							break;
                        qs.Push(pos);
						qn++;
					}
					char[] rb = new char[pos-start-2-qn];
					int k=pos-start-3-qn;
					int p = -1;
					if (qs.Count>1)
						p = qs.Pop();
					for (int j=pos-2;j>start;j--)
					{
                        if (j == p)
                            p = qs.Pop();
                        else
                            rb[k--] = input[j];
					}
					val = new TChar(new string(rb));
					return tok=Sqlx.CHARLITERAL;
				}
        /*        case '^': // ^^uri can occur in Type
                {
                    val = new TChar("");
                    tok = Sqlx.ID;
                    CheckForRdfLiteral();
                    return tok;
                } */
				case '\0':
					return tok=Sqlx.EOF;
			}
			throw new DBException("42101",ch).Mix();
		}
 /*       /// <summary>
        /// Pyrrho 4.4 if we seem to have an ID, it may be followed by ^^
        /// in which case it is an RdfLiteral
        /// </summary>
        private void CheckForRdfLiteral()
        {
            if (ch != '^')
                return;
            if (Advance() != '^')
                throw new DBException("22041", "^").Mix();
            string valu = val.ToString();
            Domain t = null;
            string iri = null;
            int pp = pos;
            Ident ic = null;
            if (Advance() == '<')
            {
                StringBuilder irs = new StringBuilder();
                while (Advance() != '>')
                    irs.Append(ch);
                Advance();
                iri = irs.ToString();
            }
    /*        else if (ch == ':')
            {
                Next();// pass the colon
                Next();
                if (tok != Sqlx.ID)
                    throw new DBException("22041", tok).Mix();
                var nsp = ctx.nsps[""];
                if (nsp == null)
                    throw new DBException("2201M", "\"\"").ISO();
                iri = nsp + val as string;
            } else 
            {
                Next();
                if (tok != Sqlx.ID)
                    throw new DBException("22041", tok).Mix();
    /*            if (ch == ':')
                {
                    Advance();
                    iri = ctx.nsps[val.ToString()];
                    if (iri == null)
                        iri = PhysBase.DefaultNamespaces[val.ToString()];
                    if (iri == null)
                        throw new DBException("2201M", val).ISO();
                    Next();
                    if (tok != Sqlx.ID)
                        throw new DBException("22041", tok).Mix();
                    iri = iri + val as string;
                } 
            }
            if (iri != null)
            {
                t = ctx.types[iri];
                if (t==null) // a surprise: ok in provenance and other Row
                {
                    t = Domain.Iri.Copy(iri);
                    ctx.types +=(iri, t);
                }
                ic = new Ident(this,Ident.IDType.Type,iri);
            }
            val = RdfLiteral.New(t, valu);
            tok = Sqlx.RDFLITERAL;
        } */
        /// <summary>
        /// This function is used for XML parsing (e.g. in XPATH)
        /// It stops at the first of the given characters it encounters or )
        /// if the stop character is unquoted and unparenthesised 
        /// ' " are processed and do not nest
        /// unquoted () {} [] &lt;&gt; nest. (Exception if bad nesting)
        /// Exception at EOF.
        /// </summary>
        /// <param name="stop">Characters to stop at</param>
        /// <returns>the stop character</returns>
        public char XmlNext(params char[] stop)
        {
            var nest = new Stack<char>();
            char quote = (char)0;
            int n = stop.Length;
            int start = pos;
            char prev = (char)0;
            for (; ; )
            {
                if (nest == null && quote == (char)0)
                    for (int j = 0; j < n; j++)
                        if (ch == stop[j])
                            goto done;
                switch (ch)
                {
                    case '\0': throw new DBException("2200N").ISO();
                    case '\\': Advance(); break;
                    case '\'': if (quote == ch)
                            quote = (char)0;
                        else if (quote==(char)0)
                            quote = ch;
                        break;
                    case '"': goto case '\'';
                    case '(':  if (quote == (char)0)
                            nest.Push(')'); 
                        break;
                    case '[': if (quote == (char)0)
                            nest.Push(']');
                        break;
                    case '{': if (quote == (char)0)
                            nest.Push('}');
                        break;
                    //     case '<': nest = MTree.Add('>', nest); break; < and > can appear in FILTER
                    case ')': if (quote==(char)0 && nest.Count==0)
                            goto done;
                        goto case ']';
                    case ']': if (quote != (char)0) break;
                        if (nest == null || ch != nest.Peek())
                            throw new DBException("2200N").ISO();
                        nest.Pop();
                        break;
                    case '}': goto case ']';
               //     case '>': goto case ']';
                    case '#': 
                        if (prev=='\r' || prev=='\n')
                            while (ch != '\r' && ch != '\n')
                                Advance();
                        break;
                }
                prev = ch;
                Advance();
            }
        done:
            val = new TChar(new string(input, start, pos - start).Trim());
            return ch;
        }
        public static string UnLex(Sqlx s)
        {
            switch (s)
            {
                default: return s.ToString();
                case Sqlx.EQL: return "=";
                case Sqlx.NEQ: return "<>";
                case Sqlx.LSS: return "<";
                case Sqlx.GTR: return ">";
                case Sqlx.LEQ: return "<=";
                case Sqlx.GEQ: return ">=";
                case Sqlx.PLUS: return "+";
                case Sqlx.MINUS: return "-";
                case Sqlx.TIMES: return "*";
                case Sqlx.DIVIDE: return "/";
            }
        }
     }
}