using System;
using Pyrrho.Common;
using System.Globalization;
using System.Text;
using Pyrrho.Level3;

// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2019
//
// This software is without support and no liability for damage consequential to use
// You can view and test this code 
// All other use or distribution or the construction of any product incorporating this technology 
// requires a license from the University of the West of Scotland
namespace Pyrrho.Level2
{
	/// <summary>
	/// A new Domain definition
	/// </summary>
	internal class PDomain : Physical
	{
        /// <summary>
        /// The defining position of the Domain
        /// </summary>
		public virtual long defpos { get { return ppos; }}
        /// <summary>
        /// The name of the Domain
        /// </summary>
        public string name;
        public Sqlx kind;
        internal bool NotNull=false;
        /// <summary>
        /// Numeric types have prec and scale attributes
        /// </summary>
        internal int prec, scale;
        /// <summary>
        /// AscDesc and Nulls control default ordering behaviour.
        /// </summary>
        internal Sqlx AscDesc = Sqlx.NULL, Nulls = Sqlx.NULL;
        /// <summary>
        /// Some attributes for date, timespan, interval etc types
        /// </summary>
        internal Sqlx start = Sqlx.NULL, end = Sqlx.NULL;
        /// <summary>
        /// The character-set attribute for a string
        /// </summary>
        internal CharSet charSet;
        /// <summary>
        /// The culture for a localised string
        /// </summary>
        internal CultureInfo culture = CultureInfo.InvariantCulture;
        internal TypedValue defaultValue = null;
        internal string defaultString = "";
        internal long structdefpos = -1; 
        internal long eltypedefpos = -1;
        public override long Dependent(Writer wr, Transaction tr)
        {
            if (defpos != ppos && !Committed(wr,defpos)) return defpos;
            var r = -1L;
            SubDependent(wr, tr, structdefpos, ref r);
            SubDependent(wr, tr, eltypedefpos, ref r);
            return r;
        }
        void SubDependent(Writer wr,Transaction tr,long pos,ref long dep)
        {
            if (dep >= 0 || pos<0)
                return;
            if (!Committed(wr, pos))
                dep = pos;
            for (var b = ((ObInfo)tr.role.obinfos[pos])?.columns.First();
                dep < 0 && b != null; b = b.Next())
            {
                var c = b.value().defpos;
                if (!Committed(wr, c))
                    dep = c;
            }
        }
        /// <summary>
        /// Constructor: a new Domain definition from the Parser
        /// </summary>
        /// <param name="t">The PDomain type</param>
        /// <param name="nm">The name of the domain, null for a standard type</param>
        /// <param name="dt">The base type</param>
        /// <param name="dl">The precision or length</param>
        /// <param name="sc">The scale</param>
        /// <param name="ch">The charset</param>
        /// <param name="co">The collation</param>
        /// <param name="dv">The default value</param>
        /// <param name="sd">The base structure definition (or -1)</param>
        /// <param name="pb">The local database</param>
        public PDomain(Type t, string nm, Sqlx dt, int dl, byte sc, CharSet ch,
            string co, string dv, long sd, Transaction tr)
            : base(t, tr)
        {
            prec = dl;
            kind = dt;
            scale = sc;
            charSet = ch;
            culture = CultureInfo.GetCultureInfo(co);
            defaultString = dv;
            NotNull = dv != "";
            if (dt == Sqlx.CHAR && dv.Length > 0 && dv[0] != '\'')
                defaultString = "'" + dv + "'";
            defaultValue = (dv == "") ? null : Domain.For(dt).Parse(tr.uid, defaultString);
            switch(dt)
            {
                default:
                    structdefpos = sd;
                    break;
                case Sqlx.ARRAY:
                case Sqlx.MULTISET:
                    eltypedefpos = sd;
                    break;
            }
            name = nm;
        }
        public PDomain(string nm, Domain dt, Transaction tr)
            : this(Type.PDomain, nm, dt, tr) { }
        protected PDomain(Type t, string nm, Domain dt,  Transaction tr)
    : base(t, tr)
        {
            prec = dt.prec;
            kind = dt.kind;
            scale = dt.scale;
            charSet = dt.charSet;
            culture = dt.culture;
            defaultString = dt.defaultString;
            defaultValue = dt.defaultValue;
            NotNull = dt.defaultValue!=TNull.Value;
            var rp = dt.representation;
            structdefpos = (rp != null && rp != Domain.Null) ? rp.defpos : -1;
            name = nm;
        }
        /// <summary>
        /// Constructor: a new Domain definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public PDomain(Reader rdr) : this(Type.PDomain,rdr) {}
        /// <summary>
        /// Constructor: a new Domain definition from the buffer
        /// </summary>
        /// <param name="t">The PDomain type</param>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		protected PDomain(Type t, Reader rdr) : base(t,rdr) {}
        protected PDomain(PDomain x, Writer wr) : base(x, wr)
        {
            name = x.name;
            kind = x.kind;
            NotNull = x.NotNull;
            prec = x.prec;
            scale = x.scale;
            AscDesc = x.AscDesc;
            Nulls = x.Nulls;
            start = x.start;
            end = x.end;
            charSet = x.charSet;
            culture = x.culture;
            defaultValue = x.defaultValue;
            structdefpos = x.structdefpos;
            eltypedefpos = x.eltypedefpos;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PDomain(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr) //LOCKED
		{
            if (kind == Sqlx.UNION)
                throw new PEException("PE916");
            wr.PutString(name);
            wr.PutInt((int)kind);
            wr.PutInt(prec);
            wr.PutInt(scale);
            wr.PutInt((int)charSet);
            wr.PutString(culture.Name);
            wr.PutString(defaultString);
            if (kind == Sqlx.ARRAY || kind == Sqlx.MULTISET)
                wr.PutLong(wr.Fix(eltypedefpos));
            else
                wr.PutLong(wr.Fix(structdefpos));
 			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            name = rdr.GetString();
            kind = (Sqlx)rdr.GetInt();
            prec = rdr.GetInt();
            scale = (byte)rdr.GetInt();
            charSet = (CharSet)rdr.GetInt();
            culture = GetCulture(rdr.GetString());
            defaultString = rdr.GetString();
            if (defaultString.Length>0 
                && kind == Sqlx.CHAR && defaultString[0] != '\'')
                defaultString = "'" + defaultString + "'";
            if (defaultString != "")
                try
                {
                    defaultValue = Domain.For(kind).Parse(rdr.db.uid, defaultString);
                }catch(Exception)
                {
                    defaultValue = TNull.Value;
                }
            var ep = rdr.GetLong();
            if (kind == Sqlx.ARRAY || kind == Sqlx.MULTISET)
                eltypedefpos = ep;
            else
                structdefpos = ep;
            base.Deserialise(rdr);
        }
        CultureInfo GetCulture(string s)
        {
            if (s == "" || s == "UCS_BINARY")
                return CultureInfo.InvariantCulture;
#if SILVERLIGHT||WINDOWS_PHONE
            return new CultureInfo(s);
#else
            return CultureInfo.GetCultureInfo(s);
#endif
        }
        public override long Conflicts(Database db, Transaction tr, Physical that)
        {
            switch(that.type)
            {
                case Type.PDomain1:
                case Type.PDomain:
                    return (name == ((PDomain)that).name) ? ppos : -1;
                case Type.PTable:
                case Type.PTable1:
                    return (name == ((PTable)that).name) ? ppos : -1;
                case Type.PView1:
                case Type.PView:
                    return (name == ((PView)that).name) ? ppos : -1;
                case Type.PRole:
                    return (name == ((PRole)that).name) ? ppos : -1;
                case Type.RestView1:
                case Type.RestView:
                    return (name == ((PRestView)that).name) ? ppos : -1;
                case Type.PType:
                    return (name == ((PType)that).name) ? ppos : -1;
                case Type.Change:
                    return (name == ((Change)that).name) ? ppos : -1;
                case Type.Drop:
                    return (defpos==((Drop)that).delpos) ? ppos:-1;
            }
            return base.Conflicts(db, tr, that);
        }
        /// <summary>
        /// A readable version of the Physical
        /// </summary>
        /// <returns>the string representation</returns>
        public override string ToString()
        {
            var sb = new StringBuilder("PDomain ");
            if (name != "") { sb.Append(name); sb.Append(": ");  }
            sb.Append(kind);
            if (NotNull) sb.Append(" not null");
            if (prec != 0) sb.Append(" p=" + prec);
            if (scale != 0) sb.Append(" s=" + scale);
            if (AscDesc != Sqlx.NULL) sb.Append(" " + AscDesc);
            if (Nulls != Sqlx.NULL) sb.Append(" nulls " + Nulls);
            if (start != Sqlx.NULL) sb.Append(" start " + start);
            if (end != Sqlx.NULL) sb.Append(" end " + end);
            if (charSet != 0) sb.Append(" " + charSet);
            if (defaultValue != null) sb.Append(" default " + defaultValue);
            if (structdefpos != -1) sb.Append(" struct=" + Pos(structdefpos));
            if (eltypedefpos != -1) sb.Append(" el=" + Pos(eltypedefpos));
            return sb.ToString();        
        }

        internal override (Database, Role) Install(Database db, Role ro, long p)
        {
            var dt = new Domain(this,db);
            var s = dt.ToString();
            var priv = Grant.Privilege.Usage | Grant.Privilege.GrantUsage;
            if (name != "")
                ro = ro + (Role.DBObjects, ro.dbobjects + (name, ppos))
                    + new ObInfo(ppos, name, null, priv);
            else
                ro += new ObInfo(ppos, s, null, priv);
            if (db.format<51 && structdefpos > 0)
                ro += (Role.DBObjects, ro.dbobjects + ("" + structdefpos, ppos));
            db = db + (ro,p) + (dt,p);
            if (!db.types.Contains(dt))
                db += (Database.Types, db.types + (dt, dt));
            return (db,ro);
        }
    }

    /// <summary>
    /// used for Interval types
    /// </summary>
    internal class PDateType : PDomain
    {
         /// <summary>
        /// Constructor: a new date type from the Parser
        /// </summary>
        /// <param name="nm">the name of the type</param>
        /// <param name="ki">the base date type</param>
        /// <param name="st">the starting field</param>
        /// <param name="en">the ending field</param>
        /// <param name="pr">the precision</param>
        /// <param name="sc">the scale</param>
        /// <param name="dv">the default value</param>
        /// <param name="pb">the local database</param>
        public PDateType(string nm, Sqlx ki, Sqlx st, Sqlx en, int pr, byte sc, string dv, 
            Transaction db)
            :base(Type.PDateType,nm,ki,pr,sc,CharSet.UCS,"",dv,-1L, db)
        {
            start = st;
            end = en;
        }
        /// <summary>
        /// Constructor: a date type from the file buffer
        /// </summary>
        /// <param name="bp">the buffer</param>
        /// <param name="pos">the buffer position</param>
        public PDateType(Reader rdr) : base(Type.PDateType, rdr) { }
        protected PDateType(PDateType x, Writer wr) : base(x, wr) {  }
        protected override Physical Relocate(Writer wr)
        {
            return new PDateType(this, wr);
        }
        /// <summary>
        /// Serialise a date type descriptor
        /// </summary>
        /// <param name="r">the relocation information</param>
        public override void Serialise(Writer wr) //locked
        {
            wr.PutInt(IntervalPart(start));
            wr.PutInt(IntervalPart(end));
            base.Serialise(wr);
        }
        /// <summary>
        /// Deserialise a date type descriptor
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            Sqlx start = GetIntervalPart(rdr);
            Sqlx end = GetIntervalPart(rdr);
            base.Deserialise(rdr);
        }
        /// <summary>
        /// Facilitate quick decoding of the interval fields
        /// </summary>
        internal static Sqlx[] intervalParts = new Sqlx[] { Sqlx.YEAR, Sqlx.MONTH, Sqlx.DAY, Sqlx.HOUR, Sqlx.MINUTE, Sqlx.SECOND };
        /// <summary>
        /// helper for encoding interval fields
        /// </summary>
        /// <param name="e">YEAR, MONTH, DAY, HOUR, MINUTE, SECOND</param>
        /// <returns>corresponding integer 0,1,2,3,4,5</returns>
        internal static int IntervalPart(Sqlx e)
        {
            switch (e)
            {
                case Sqlx.YEAR: return 0;
                case Sqlx.MONTH: return 1;
                case Sqlx.DAY: return 2;
                case Sqlx.HOUR: return 3;
                case Sqlx.MINUTE: return 4;
                case Sqlx.SECOND: return 5;
            }
            return -1;
        }
        Sqlx GetIntervalPart(Reader rdr)
        {
            int j = rdr.GetInt();
            if (j < 0)
                return Sqlx.NULL;
            return intervalParts[j];
        }
        public override string ToString()
        {
            return "PDateType " + base.ToString();
        }
    }

    internal class PDomain1 : PDomain
    {
        public PDomain1(string nm, Domain dt, Transaction tr)
            : base(Type.PDomain1, nm, dt, tr) { }
        /// <summary>
        /// Constructor: a data type from the file buffer
        /// </summary>
        /// <param name="bp">the buffer</param>
        /// <param name="pos">the buffer position</param>
        public PDomain1(Reader rdr) : this(Type.PDomain1, rdr) { }
        protected PDomain1(Type t, Reader rdr) : base(t, rdr) { }
        protected PDomain1(PDomain1 x, Writer wr) : base(x, wr) {  }
        protected override Physical Relocate(Writer wr)
        {
            return new PDomain1(this, wr);
        }
        /// <summary>
        /// Deserialise an OWL domain descriptor
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            string ur = rdr.GetString();
            string ab = rdr.GetString();
            base.Deserialise(rdr);
        }
        public override string ToString()
        {
            return "PDomain1 " + base.ToString();
        }
    }
}
