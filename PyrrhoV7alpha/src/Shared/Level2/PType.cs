using Pyrrho.Common;
using Pyrrho.Level3;
using Pyrrho.Level4;
using Pyrrho.Level5;
using System.Text;

// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2023
//
// This software is without support and no liability for damage consequential to use.
// You can view and test this code
// You may incorporate any part of this code in other software if its origin 
// and authorship is suitably acknowledged.

namespace Pyrrho.Level2
{
	/// <summary>
	/// Basic structured type support
	/// Similar information is specified for a Type as for a Domain with the following additions
	///		under	subtype info: may be -1 if not a subtype
	///		representation	uses structDef field in Domain
	///	so attributes are TableColumns of the referenced PTable
	/// </summary>
	internal class PType : Compiled
	{
        internal Domain? under = null;
        internal virtual long defpos => ppos;
        /// <summary>
        /// Constructor: A user-defined type definition from the Parser.
        /// </summary>
        /// <param name="t">The PType type</param>
        /// <param name="nm">The name of the new type</param>
        /// <param name="dt">The representation datatype</param>
        /// <param name="db">The local database</param>
        protected PType(Type t, string nm, UDType dm, Domain? un, long ns, long pp, Context cx)
            : base(t, pp, cx, nm, dm, ns)
        {
            name = nm;
            var dm1 = (t==Type.EditType)? dm: (Domain)dm.Relocate(pp);
            dataType = dm1 + (ObInfo.Name,nm);
            under = un;
            if (un?.defpos>0)
                dataType += (Domain.Under, un);
        }
        public PType(string nm, UDType dm, Domain? un, long ns, long pp, Context cx)
            : this(Type.PType, nm, dm, un, ns, pp, cx) { }
        /// <summary>
        /// Constructor: A user-defined type definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public PType(Reader rdr) : base(Type.PType,rdr) 
        {
            dataType = Domain.TypeSpec;
        }
        /// <summary>
        /// Constructor: A user-defined type definition from the buffer
        /// </summary>
        /// <param name="t">The PType type</param>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		protected PType(Type t, Reader rdr) : base(t,rdr) 
        { }
        protected PType(PType x, Writer wr) : base(x, wr)
        {
            name = wr.cx.NewNode(wr.Length,x.name.Trim(':'));
            if (x.name.EndsWith(':'))
                name += ':';
            under = (UDType?)x.under?.Fix(wr.cx);
            dataType = (UDType)dataType.Fix(wr.cx);
        }
        public override long Dependent(Writer wr, Transaction tr)
        {
            if (under != null && !Committed(wr, under.defpos)) return under.defpos;
            return -1L;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PType(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
		public override void Serialise(Writer wr)
		{
            wr.PutLong(under?.defpos??-1L); 
            // copied from PDomain.Serialise
            wr.PutString(name);
            wr.PutInt((int)dataType.kind);
            wr.PutInt(dataType.prec);
            wr.PutInt(dataType.scale);
            wr.PutInt((int)dataType.charSet);
            wr.PutString(dataType.culture.Name);
            wr.PutString(dataType.defaultString);
            wr.PutLong(-1L);
			base.Serialise(wr);
		}
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            var un = rdr.GetLong();
            name = rdr.GetString();
            var m = dataType.mem; // the Old domain for EditType, otherwise Content + PNodeType and PEdgeType things
            var k = (Sqlx)rdr.GetInt();
            m = m + (Domain.Precision, rdr.GetInt())
                + (Domain.Scale, rdr.GetInt())
                + (DBObject.Definer, rdr.context.role.defpos)
                + (Domain.Charset, (CharSet)rdr.GetInt())
                + (Domain.Culture, PDomain.GetCulture(rdr.GetString()));
            var oi = new ObInfo(name, Grant.AllPrivileges);
            var ds = rdr.GetString();
            if (ds.Length > 0
                && k == Sqlx.CHAR && ds[0] != '\'')
            {
                ds = "'" + ds + "'";
                m += (Domain.DefaultString, ds);
            }
            var st = rdr.GetLong(); // a relic of the past
            var dt = dataType;
            m = m + (Domain.Representation, dt.representation) + (Domain.RowType, dt.rowType);
            var ns = BTree<string,(int,long?)>.Empty;
            for (var b = dt.rowType.First(); b != null; b = b.Next())
                if (b.value() is long p && rdr.context.NameFor(p) is string n)
                    ns += (n, (b.key(), p));
            m = m + (ObInfo.Name, name) + (Domain.Kind, k);
            if (dt.super is not null)
                m += (Domain.Under, dt.super);
            if (un > 0)
            { // it can happen that under is more recent than dt (EditType), so be careful
                under = (UDType)(rdr.context.db.objects[un] ?? Domain.TypeSpec);
                var ui = under.infos[rdr.context.role.defpos];
                var rs = dt.representation;
                for (var b = ui?.names.First(); b != null; b = b.Next())
                    if (ns[b.key()].Item2 is long p)
                    {
                        rs -= p;
                        ns -= b.key();
                    }
                var tr = BTree<int, long?>.Empty;
                for (var b = ns.First(); b != null; b = b.Next())
                    tr += b.value();
                var nrt = BList<long?>.Empty;
                if (dt != null)
                    m += (Domain.Representation, rs);
                for (var b = tr.First(); b is not null; b = b.Next())
                    if (b.value() is long p && rs.Contains(p))
                        nrt += p;
                m += (Domain.RowType, nrt);
                m += (Domain.Under, under);
            }
            oi += (ObInfo.Names, ns);
            m += (DBObject.Infos, new BTree<long, ObInfo>(rdr.context.role.defpos, oi));
            dataType = k switch
            {
                Sqlx.TYPE => new UDType(defpos, m),
                Sqlx.NODETYPE => new NodeType(defpos, m),
                Sqlx.EDGETYPE => new EdgeType(defpos,m,this),
                _ => Domain.Null
            };
            base.Deserialise(rdr);
        }
        /// <summary>
        /// A readable version of the Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString() 
		{
            var sb = new StringBuilder(base.ToString());
            if (under != null)
            { sb.Append(" Under: "); sb.Append(DBObject.Uid(under.defpos)); }
            return sb.ToString();
		}
        public override DBException? Conflicts(Database db, Context cx, Physical that, PTransaction ct)
        {
            var nm = dataType.name;
            switch(that.type)
            {
                case Type.PType:
                case Type.PType1:
                case Type.PNodeType:
                case Type.PEdgeType:
                    if (nm == ((PType)that).dataType.name)
                        return new DBException("40022", nm, that, ct);
                    break;
                case Type.PDomain1:
                case Type.PDomain:
                    var tn = ((PDomain)that).name;
                    if (nm == tn)
                        return new DBException("40022", nm, tn, ct);
                    break;
                case Type.PTable:
                case Type.PTable1:
                    if (dataType.name == ((PTable)that).name)
                        return new DBException("40032", nm, that, ct);
                    break;
                case Type.PView:
                    if (nm == ((PView)that).name)
                        return new DBException("40032", nm, that, ct);
                    break;
                case Type.PRole1:
                case Type.PRole:
                    if (nm == ((PRole)that).name)
                        return new DBException("40035", nm, that, ct);
                    break;
                case Type.RestView1:
                case Type.RestView:
                    if (nm == ((PRestView)that).name)
                        return new DBException("40032", nm, that, ct);
                    break;
                case Type.Change:
                    if (nm == ((Change)that).name)
                        return new DBException("40032", nm, that, ct);
                    break;
                case Type.Drop:
                    if (ppos == ((Drop)that).delpos)
                        return new DBException("40016", nm, that, ct);
                    break;
            }
            return base.Conflicts(db, cx, that, ct);
        }
        internal override DBObject Install(Context cx, long p)
        {
            var ro = cx.role;
            var priv = Grant.Privilege.Owner | Grant.Privilege.Insert | Grant.Privilege.Select |
                Grant.Privilege.Update | Grant.Privilege.Delete |
                Grant.Privilege.GrantDelete | Grant.Privilege.GrantSelect |
                Grant.Privilege.GrantInsert |
                Grant.Privilege.Usage | Grant.Privilege.GrantUsage;
            var oi = new ObInfo(name, priv);
            oi += (ObInfo.SchemaKey, p);
            var ns = BTree<string, (int, long?)>.Empty;
            for (var c = dataType as UDType; c != null; c = cx.db.objects[c.super?.defpos ?? -1L] as UDType)
                for (var b = c.rowType.First(); b != null; b = b.Next())
                    if (b.value() is long bp && cx.obs[bp] is TableColumn tc)
                        ns += (tc.infos[tc.definer]?.name ?? "??", (b.key(), tc.defpos));
            oi += (ObInfo.Names, ns);
            if (dataType is EdgeType et && this is PEdgeType pe)
            {
                // the first edgetype with a given name has the alias table for any others
                var np = defpos;
                if (ro.dbobjects[name] is long pp && cx.db.objects[pp] is EdgeType)
                    np = pp;
                else
                    ro += (Role.DBObjects, ro.dbobjects + (name, defpos));
                cx.db += (np, pe.leavingType, pe.arrivingType, et.defpos);
            }
            else
                ro += (Role.DBObjects, ro.dbobjects + (name, defpos));
            var os = new BTree<long, ObInfo>(Database._system.role.defpos, oi)
                + (ro.defpos, oi);
            if (dataType is UDType ut && cx.db.objects[ut.super?.defpos ?? -1L] is UDType tu)
            {
                dataType = tu.Inherit(ut);
                dataType += (Table.PathDomain, ((UDType)dataType)._PathDomain(cx));
                tu += (Domain.Subtypes, tu.subtypes + (defpos, true));
                cx.db += (tu, p);
                for (var b = tu.subtypes.First(); b != null; b = b.Next())
                    if (cx.db.objects[b.key()] is UDType at)
                    {
                        at += (Domain.Under, tu);
                        cx.db += (at.defpos, at);
                    }
                dataType += (Domain.Under, tu);
            }
   //         if (dataType is NodeType && !cx.db.graphUsage.Contains(defpos))
   //             cx.db += (Database.GraphUsage, cx.db.graphUsage + (defpos, new CTree<long, bool>(defpos, true)));
            dataType = dataType + (DBObject.Infos, os) + (DBObject.Definer, cx.role.defpos);
            cx.Add(dataType);
            if (cx.db.format < 51)
                ro += (Role.DBObjects, ro.dbobjects + ("" + defpos, defpos));
            cx.db = cx.db + (ro, p) + dataType;
            if (cx.db.mem.Contains(Database.Log))
                cx.db += (Database.Log, cx.db.log + (ppos, type));
            return (Domain)dataType.Fix(cx);
        }
    }
    internal class PType1 : PType // retained but no longer used
    {
        protected PType1(Type t, string nm, UDType dm, Domain? un, long ns, long pp, Context cx)
            :base(t,nm,dm,un, ns, pp, cx) { }
        /// <summary>
        /// Constructor: A user-defined type definition from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public PType1(Reader rdr) : base(Type.PType1,rdr) {}
        protected PType1(Type t,Reader rdr) : base(t,rdr) { }
        protected PType1(PType1 x, Writer wr) : base(x, wr)
        { }
        protected override Physical Relocate(Writer wr)
        {
            return new PType1(this, wr);
        }
        public override void Serialise(Writer wr)
        {
            wr.PutString("");
            base.Serialise(wr);
        }
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            rdr.GetString();
            base.Deserialise(rdr);
        }
    }

 }
