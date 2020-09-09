using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Pyrrho.Common;
using Pyrrho.Level1;
using Pyrrho.Level3;
using Pyrrho.Level4;

// Pyrrho Database Engine by Malcolm Crowe at the University of the West of Scotland
// (c) Malcolm Crowe, University of the West of Scotland 2004-2020
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
	/// Modify is used for changes to procs, methods, functions, and views.
    /// Extend this if the syntax ever allows ALTER for triggers, views, checks, or indexes (!)
	/// </summary>
	internal class Modify : Compiled
	{
        /// <summary>
        /// The object being modified
        /// </summary>
		public long modifydefpos;
        /// <summary>
        /// The new name of the routine
        /// </summary>
		public string name;
        /// <summary>
        /// The new parameters and body of the routine
        /// </summary>
		public string body;
        /// <summary>
        /// The Parsed version of the body for the definer's role
        /// </summary>
        public DBObject now;
        public override long Dependent(Writer wr, Transaction tr)
        {
            if (!Committed(wr,modifydefpos)) return modifydefpos;
            return -1;
        }
        /// <summary>
        /// Constructor: A Modify request from the parser
        /// </summary>
        /// <param name="nm">The (new) name of the routine</param>
        /// <param name="dp">The defining position of the routine</param>
        /// <param name="pc">The (new) parameters and body of the routine</param>
        /// <param name="pb">The local database</param>
        public Modify(string nm, long dp, string pc, DBObject nw, long pp, Context cx)
            : base(Type.Modify,pp,cx)
		{
            modifydefpos = dp;
            name = nm;
            body = pc;
            now = nw?? throw new PEException("PE919");
        }
        /// <summary>
        /// Constructor: A Modify request from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public Modify(Reader rdr) : base(Type.Modify,rdr) {}
        protected Modify(Modify x, Writer wr) : base(x, wr)
        {
            modifydefpos = wr.Fix(x.modifydefpos);
            name = x.name;
            body = x.body;
            now = x.now;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new Modify(this, wr);
        }
        /// <summary>
        /// Serialise this Physical to the PhyBase
        /// </summary>
        /// <param name="r">Relocation information for the positions</param>
        public override void Serialise(Writer wr) 
		{
			modifydefpos = wr.Fix(modifydefpos);
            wr.PutLong(modifydefpos);
            wr.PutString(name);
            wr.PutString(body);
			base.Serialise(wr);
            var pp = wr.cx.db.objects[modifydefpos] as Procedure;
            pp += (Procedure.Clause, body);
            wr.cx.Install(pp,wr.cx.db.loadpos);
        }
        /// <summary>
        /// Desrialise this physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
		{
			modifydefpos = rdr.GetLong();
			name = rdr.GetString();
			body = rdr.GetString();
			base.Deserialise(rdr);
            switch (name)
            {
                default:
                    {
                        var up = rdr.context.db.role.dbobjects[name];
                        var oi = (ObInfo)rdr.context.db.role.infos[up];
                        var udt = (UDType)oi.domain;
                        var psr = new Parser(rdr.context, new Ident(body, ppos + 2));
                        var (_,xp) = psr.ParseProcedureHeading(new Ident(name, ppos+1));
                        for (var b = udt.representation.First(); b != null; b = b.Next())
                        {
                            var p = b.key();
                            var ic = new Ident(psr.cx.Inf(p).name, p);
                            psr.cx.defs += (ic, p);
                            psr.cx.Add(new SqlValue(ic) + (DBObject._Domain, b.value()));
                        }
                        now = psr.ParseProcedureStatement(xp);
                        framing = new Framing(psr.cx);
                        break;
                    }
                case "Source":
                    {
                        var ps = rdr.context.db.objects[modifydefpos] as Procedure;
                        now = new Parser(rdr.context).ParseQueryExpression(body, ps.domain);
                        break;
                    }
                case "Insert": // we ignore all of these (PView1)
                case "Update":
                case "Delete":
                    now = null;
                    break;
            }
		}
        internal override void OnLoad(Reader rdr)
        {
            if (now == null)
                return;
            var psr = new Parser(rdr.context);
            var pr = (Method)rdr.context.db.objects[ppos];
            psr.cx.srcFix = ppos + 1;
            rdr.context.obs += (pr.defpos, pr + (Procedure.Body, now));
        }
        public override DBException Conflicts(Database db, Context cx, Physical that, PTransaction ct)
        {
            switch(that.type)
            {
                case Type.Grant:
                    {
                        var g = (Grant)that;
                        if (modifydefpos == g.obj || modifydefpos == g.grantee)
                            return new DBException("40051", modifydefpos, that, ct);
                        break; 
                    }
                case Type.Drop:
                    if (modifydefpos == ((Drop)that).delpos)
                        return new DBException("40010", modifydefpos, that, ct);
                    break;
                case Type.Modify:
                    {
                        var m = (Modify)that;
                        if (name == m.name || modifydefpos == m.modifydefpos)
                            return new DBException("40052", ppos, that, ct);
                        break;
                    }
            }
            return base.Conflicts(db, cx, that, ct);
        }
        /// <summary>
        /// A readable version of the Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString()
		{
			return "Modify "+Pos(modifydefpos)+": "+name+" to "+body;
		}

        internal override void Install(Context cx, long p)
        {
            ((DBObject)cx.db.objects[modifydefpos])?.Modify(cx, now, p);
            var ob = ((DBObject)cx.db.objects[modifydefpos])??now;
            cx.obs += (modifydefpos,ob);
            cx.db += (Database.Log, cx.db.log + (ppos, type));
        }
    }
}
