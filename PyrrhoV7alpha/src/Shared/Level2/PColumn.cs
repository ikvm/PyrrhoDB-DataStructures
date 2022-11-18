using System;
using System.Text;
using Pyrrho.Level3;
using Pyrrho.Level4;
using Pyrrho.Common;
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
namespace Pyrrho.Level2
{
	/// <summary>
	/// A PColumn belongs to a PTable and has a name, a sequence no, and a domain
	/// Both domains and TableColumns have check constraints, defaults and collates
	/// Though this seems redundant it is asking for trouble not to respect this SQL convention
	/// in the database structs. (Actually domain defaults are more restrictive.)
	/// Columns may have a notNull constraint and integrity, uniqueness and referential constraints.
    /// Obsolete: see PColumn2
	/// </summary>
	internal class PColumn : Compiled
	{
        /// <summary>
        /// The Table
        /// </summary>
		public Table table;
        public long tabledefpos;
        /// <summary>
        /// The name of the TableColumn
        /// </summary>
		public string name;
        /// <summary>
        /// The position in the table (this matters for select * from ..)
        /// </summary>
		public int seq;
        public virtual long defpos => ppos;
        /// <summary>
        /// The defining position of the domain
        /// </summary>
        public long domdefpos = -1L;
		public TypedValue dv => dataType?.defaultValue??TNull.Value; 
        public string dfs,ups;
        public BTree<UpdateAssignment,bool> upd = CTree<UpdateAssignment,bool>.Empty; // see PColumn3
		public bool notNull = false;    // ditto
		public GenerationRule generated = GenerationRule.None; // ditto
        public override long Dependent(Writer wr, Transaction tr)
        {
            if (!Committed(wr,table.defpos)) return table.defpos;
            dataType.Create(wr, tr);
            return -1;
        }
        /// <summary>
        /// Constructor: A new Column definition from the Parser
        /// </summary>
        /// <param name="t">The PColumn type</param>
        /// <param name="pr">The table</param>
        /// <param name="nm">The name of the columns</param>
        /// <param name="sq">The 0-based position in the table</param>
        /// <param name="dm">The domain</param>
        /// <param name="tb">The local database</param>
        public PColumn(Type t, Table pr, string nm, int sq, Domain dm, long pp, 
            Context cx,long tc) : base(t,pp,cx,tc,dm)
		{
			table = pr;
			name = nm;
			seq = sq;
            tabledefpos = pr.defpos;
            dataType = dm;
		}
        /// <summary>
        /// Constructor: a new Column definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public PColumn(Reader rdr) : base (Type.PColumn,rdr){}
        /// <summary>
        /// Constructor: a new Column definition from the buffer
        /// </summary>
        /// <param name="t">The PColumn type</param>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		protected PColumn(Type t,Reader rdr) : base(t,rdr) {}
        protected PColumn(PColumn x, Writer wr) : base(x, wr)
        {
            table = (Table)x.table._Relocate(wr.cx);
            tabledefpos = table.defpos;
            name = x.name;
            seq = x.seq;
            dataType = (Domain)x.dataType.Relocate(wr.cx);
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PColumn(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
		{
			table = (Table)table._Relocate(wr.cx);
            tabledefpos = table.defpos;
			dataType = wr.cx.db.Find(dataType);
            wr.PutLong(table.defpos);
            wr.PutString(name.ToString());
            wr.PutInt(seq);
            wr.PutLong(wr.cx.db.types[dataType]);
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            tabledefpos = rdr.GetLong();
            table = (Table)rdr.GetObject(tabledefpos);
            name = rdr.GetString();
            seq = rdr.GetInt();
            domdefpos = rdr.GetLong();
            dataType = (Domain)rdr.GetObject(domdefpos);
            base.Deserialise(rdr);
        }
        public override DBException Conflicts(Database db, Context cx, Physical that, PTransaction ct)
        {
            switch(that.type)
            {
                case Type.PColumn3:
                case Type.PColumn2:
                case Type.PColumn:
                    if (table.defpos == ((PColumn)that).table.defpos)
                        return new DBException("40025", defpos, that, ct);
                    break;
                case Type.Alter3:
                    {
                        var a = (Alter3)that;
                        if (table.defpos == a.table.defpos && name == a.name)
                            return new DBException("40025", table.defpos, that, ct);
                        break;
                    }
                case Type.Alter2:
                    {
                        var a = (Alter2)that;
                        if (table.defpos == a.table.defpos && name == a.name)
                            return new DBException("40025", defpos, that, ct);
                        break;
                    }
                case Type.Alter:
                    {
                        var a = (Alter)that;
                        if (table.defpos == a.table.defpos && name == a.name)
                            return new DBException("40025", defpos, that, ct);
                        break;
                    }
                case Type.Drop:
                    {
                        var d = (Drop)that;
                        if (table.defpos == d.delpos)
                            return new DBException("40012", table.defpos, that, ct);
                        if (cx.db.types[dataType] == d.delpos)
                            return new DBException("40016", defpos, that, ct);  
                        break;
                    }
            }
            return base.Conflicts(db, cx, that, ct);
        }
        internal int flags
        {
            get
            {
                return (notNull ? 0x100 : 0) + ((generated.gen == 0) ? 0 : 0x200);
            }
        }
        /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString()
        {
            var sb = new StringBuilder(GetType().Name);
            sb.Append(" "); sb.Append(name); sb.Append(" for ");
            sb.Append(Pos(tabledefpos));
            sb.Append("("); sb.Append(seq); sb.Append(")[");
            if (domdefpos >= 0)
                sb.Append(DBObject.Uid(domdefpos));
            else
                sb.Append(dataType); 
            sb.Append("]");
            return sb.ToString();
        }
        internal override void Install(Context cx, long p)
        {
            table = (Table)cx.db.objects[table.defpos];
            var ro = (table is VirtualTable)?Database._system.role:cx.db.role;
            cx.Install(dataType, p);
            var tc = new TableColumn(table, this, dataType,cx.role);
            var rp = ro.defpos;
            var priv = table.infos[rp].priv & ~(Grant.Privilege.Delete | Grant.Privilege.GrantDelete);
            var oc = new ObInfo(name, priv);
            tc += (DBObject.Infos, tc.infos + (rp, oc)); // table name will already be known
            cx.Add(tc);
            if (table.defpos<0)
                throw new DBException("42105");
            var op = cx.parse;
            cx.parse = ExecuteStatus.Compile;
            var ov = cx.val;
            cx.val = TNull.Value;
            var dm = cx._Dom(table);
            if (dm.defpos < 0)
            {
                dm = (Domain)dm.Relocate(cx.GetUid());
                cx.Add(dm);
                table += (DBObject._Domain, dm.defpos); 
            }
            dm = new Domain(table.domain, cx, Sqlx.TABLE,
                dm.representation + (tc.defpos, dataType), dm.rowType + tc.defpos);
            cx.Add(dm);
            table += (cx,tc); 
            table += (DBObject._Framing,table.framing+dm);
            table += (DBObject.LastChange, ppos);
            cx.parse = op;
            cx.val = ov;
            if (cx.db is Transaction tr && tr.physicals[table.defpos] is Compiled pt)
                pt.framing = table.framing;
            cx.Add(table);
            cx.obs += table.framing.obs;
            if (cx.db.format < 51)
            {
                ro += (Role.DBObjects, ro.dbobjects + ("" + defpos, defpos));
                cx.db += (ro, p);
            }
            if (cx.db.mem.Contains(Database.Log))
                cx.db += (Database.Log, cx.db.log + (ppos, type));
            cx.Install(table,p);
            cx.Install(tc,p);
            base.Install(cx, p);
        }
    }
    /// <summary>
    /// PColumn2: this is an extension of PColumn to add some column constraints
    /// For a general description see PColumn
    /// </summary>
	internal class PColumn2 : PColumn
	{
        /// <summary>
        /// Constructor: A new Column definition from the Parser
        /// </summary>
        /// <param name="pr">The table</param>
        /// <param name="nm">The name of the column (may be null)</param>
        /// <param name="sq">The position of the column in the table</param>
        /// <param name="dm">The domain</param>
        /// <param name="dv">The default value</param>
        /// <param name="nn">True if the NOT NULL constraint is to apply</param>
        /// <param name="ge">The generation rule</param>
        /// <param name="db">The local database</param>
        public PColumn2(Table pr, string nm, int sq, Domain dm, string ds, TypedValue dv, 
            bool nn, GenerationRule ge, long pp, Context cx)
            : this(Type.PColumn2,pr,nm,sq,dm,ds,dv,nn,ge,pp,cx)
		{ }
        /// <summary>
        /// Constructor: A new Column definition from the Parser
        /// </summary>
        /// <param name="t">the PColumn2 type</param>
        /// <param name="pr">The table</param>
        /// <param name="nm">The name of the ident</param>
        /// <param name="sq">The position of the ident in the table</param>
        /// <param name="dm">The domain</param>
        /// <param name="ds">The default value</param>
        /// <param name="nn">True if the NOT NULL constraint is to apply</param>
        /// <param name="ge">the Generation Rule</param>
        /// <param name="db">The database</param>
        protected PColumn2(Type t, Table pr, string nm, int sq, Domain dm, string ds,
            TypedValue v, bool nn, GenerationRule ge, long pp, Context cx)
            : base(t,pr,nm,sq,dm+(Domain.Default,v),pp,cx,ge.target)
		{
			dfs = ds;
			notNull = nn;
			generated = ge;
		}
        /// <summary>
        /// Constructor: A new Column definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public PColumn2(Reader rdr) : this(Type.PColumn2,rdr){}
        /// <summary>
        /// Constructor: A new Column definition from the buffer
        /// </summary>
        /// <param name="t">The PColumn2 type</param>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
        protected PColumn2(Type t, Reader rdr) : base(t, rdr) { }
        protected PColumn2(PColumn2 x, Writer wr) : base(x, wr)
        {
            dfs = x.dfs;
            notNull = x.notNull;
            generated = x.generated;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PColumn2(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
		{
            wr.PutString(dfs.ToString());
            wr.PutInt(notNull ? 1 : 0);
            wr.PutInt((int)generated.gen);
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr) 
		{
            var dfsrc = new Ident(rdr.GetString(), rdr.context.Ix(ppos + 1));
            dfs = dfsrc.ident;
            notNull = (rdr.GetInt() != 0);
			var gn = (Generation)rdr.GetInt();
            base.Deserialise(rdr);
            if (dfs != "")
            {
                if (gn != Generation.Expression)
                {
                    var dm = (Domain)rdr.context.db.objects[domdefpos];
                    dataType = dm+ (Domain.Default,dm.Parse(rdr.Position, dfs, rdr.context))
                        +(Domain.DefaultString,dfs);
                }
                else
                    generated = new GenerationRule(Generation.Expression,
                        dfs, new SqlNull(rdr.Position), defpos);
            }
        }
        internal override void OnLoad(Reader rdr)
        {
            if (generated.gen == Generation.Expression)
            {
                table = (Table)rdr.context.db.objects[table.defpos];
                var psr = new Parser(rdr, new Ident(dfs, rdr.context.Ix(ppos + 1)), table); // calls ForConstraintParse
                var nst = psr.cx.db.nextStmt;
                var sv = psr.ParseSqlValue(dataType);
                psr.cx.Add(sv);
                framing = new Framing(psr.cx,nst);
                generated += (GenerationRule.GenExp, sv.defpos);
            }
        }
        public override string ToString()
        {
            var sb = new StringBuilder(base.ToString());
            if (dfs != "") { sb.Append(" default="); sb.Append(dfs); }
            if (notNull) sb.Append(" NOT NULL");
            if (generated.gen != Generation.No) { sb.Append(" Generated="); sb.Append(generated.gen); }
            return sb.ToString();
        }
	}
    /// <summary>
    /// PColumn3: this is an extension of PColumn to add some column constraints.
    /// Specifically we add the readonly constraint
    /// For a general description see PColumn
    /// </summary>
    internal class PColumn3 : PColumn2
    {
        /// <summary>
        /// Constructor: A new Column definition from the Parser
        /// </summary>
        /// <param name="pr">The defining position of the table</param>
        /// <param name="nm">The name of the table column</param>
        /// <param name="sq">The position of the table column in the table</param>
        /// <param name="dm">The domain</param>
        /// <param name="dv">The default value</param>
        /// <param name="ua">The update assignments</param>
        /// <param name="nn">True if the NOT NULL constraint is to apply</param>
        /// <param name="ge">The generation rule</param>
        /// <param name="db">The local database</param>
        public PColumn3(Table pr, string nm, int sq, Domain dm, string ds, TypedValue dv, 
            string us, CTree<UpdateAssignment,bool> ua, bool nn, GenerationRule ge, long pp,
            Context cx)
            : this(Type.PColumn3, pr, nm, sq, dm, ds, dv, us, ua, nn, ge, pp, cx)
        { }
        /// <summary>
        /// Constructor: A new Column definition from the Parser
        /// </summary>
        /// <param name="t">the PColumn2 type</param>
        /// <param name="pr">The defining position of the table</param>
        /// <param name="nm">The name of the ident</param>
        /// <param name="sq">The position of the ident in the table</param>
        /// <param name="dm">The domain</param>
        /// <param name="dv">The default value</param>
        /// <param name="nn">True if the NOT NULL constraint is to apply</param>
        /// <param name="ge">The generation rule</param>
        /// <param name="db">The local database</param>
        protected PColumn3(Type t, Table pr, string nm, int sq, Domain dm, string ds, 
            TypedValue dv, string us, CTree<UpdateAssignment,bool> ua, bool nn, 
            GenerationRule ge, long pp, Context cx)
            : base(t, pr, nm, sq, dm, ds, dv, nn, ge, pp, cx)
        {
            upd = ua;
            ups = us;
        }
        /// <summary>
        /// Constructor: A new Column definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
        public PColumn3(Reader rdr) : this(Type.PColumn3, rdr) { }
        /// <summary>
        /// Constructor: A new Column definition from the buffer
        /// </summary>
        /// <param name="t">The PColumn2 type</param>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
        protected PColumn3(Type t, Reader rdr) : base(t, rdr) { }
        protected PColumn3(PColumn3 x, Writer wr) : base(x, wr)
        {
            upd = x.upd;
            ups = x.ups;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PColumn3(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
        public override void Serialise(Writer wr)
        {
            wr.PutString(ups??""); 
            wr.PutLong(-1);// backwards compatibility
            wr.PutLong(-1);
            wr.PutLong(-1);
            base.Serialise(wr);
        }
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            ups = rdr.GetString();
            rdr.Upd(this);
            rdr.GetLong();
            rdr.GetLong();
            rdr.GetLong();
            base.Deserialise(rdr);
        }
        public override string ToString()
        {
            var sb = new StringBuilder(base.ToString());
            if (upd != CTree<UpdateAssignment,bool>.Empty) { sb.Append(" UpdateRule="); sb.Append(upd); }
            return sb.ToString();
        }
    }
    /// <summary>
    /// Pyrrho 5.1. To allow constraints (even Primary Key) to refer to deep structure.
    /// This feature is introduced for Documents but will be used for row type columns, UDTs etc.
    /// </summary>
    internal class PColumnPath : Physical
    {
        /// <summary>
        /// The defining position of the Column
        /// </summary>
        public virtual long defpos { get { return ppos; } }
        /// <summary>
        /// The selector to which this path is appended
        /// </summary>
        public long coldefpos;
        /// <summary>
        /// a single component of the ColumnPath string
        /// </summary>
        public string path = null;
        /// <summary>
        /// The domain if known
        /// </summary>
        public long domdefpos;
        public override long Dependent(Writer wr, Transaction tr)
        {
            if (defpos!=ppos && !Committed(wr,defpos)) return defpos;
            if (!Committed(wr,coldefpos)) return coldefpos;
            if (!Committed(wr,domdefpos)) return domdefpos;
            return -1;
        }
        /// <summary>
        /// Constructor: A ColumnmPath definition from the Parser
        /// </summary>
        /// <param name="co">The Column</param>
        /// <param name="pa">The path string</param>
        /// <param name="dm">The domain defining position</param>
        /// <param name="db">The local database</param>
        public PColumnPath(long co, string pa, long dm, long pp, Context cx)
            : base(Type.ColumnPath, pp, cx)
        { 
            coldefpos = co;
            path = pa;
            domdefpos = dm;
        }
        /// <summary>
        /// Constructor: from the file buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
        public PColumnPath(Reader rdr) : base(Type.ColumnPath, rdr) { }
        public override void Serialise(Writer wr)
        {
            coldefpos = wr.cx.Fix(coldefpos);
            domdefpos = wr.cx.Fix(domdefpos);
            wr.PutLong(coldefpos);
            wr.PutString(path);
            wr.PutLong(domdefpos);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            coldefpos = rdr.GetLong();
            path = rdr.GetString();
            domdefpos = rdr.GetLong();
            base.Deserialise(rdr);
        }
        public override string ToString()
        {
            return "ColumnPath [" + coldefpos + "]" + path + "(" + domdefpos + ")";
        }

        internal override void Install(Context cx, long p)
        {
            throw new NotImplementedException();
        }

        protected override Physical Relocate(Writer wr)
        {
            throw new NotImplementedException();
        }
    }
}
