using System.Text;
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
    /// A Level 2 user definition. User identities are obtained from the operating system and from Grants
    /// </summary>
    internal class PUser : Physical
    {
        /// <summary>
        /// The name of the user
        /// </summary>
		public string name;
        public override long Dependent(Writer wr, Transaction tr)
        {
            return -1;
        }
        /// <summary>
        /// Constructor: A user identity from the Parser (e.g. from GRANT)
        /// </summary>
        /// <param name="nm">The name of the user (an identifier)</param>
        /// <param name="db">The local database</param>
        public PUser(string nm, long pp, Context cx)
            : this(Type.PUser, nm, pp, cx)
        {
        }
        /// <summary>
        /// Constructor: A user identity from the Parser (e.g. GRANT)
        /// </summary>
        /// <param name="tp">The PUser type</param>
        /// <param name="nm">The name of the user (an identifier)</param>
        /// <param name="pb">The local database</param>
        protected PUser(Type tp, string nm, long pp, Context cx)
            : base(tp, pp, cx)
        {
            name = nm;
        }
        /// <summary>
        /// Constructor: A Physical from the buffer
        /// </summary>
        /// <param name="bp">the buffer</param>
        /// <param name="pos">the defining position in the buffer</param>
        public PUser(Reader rdr) : base(Type.PUser, rdr) { }
        protected PUser(PUser x, Writer wr) : base(x, wr)
        {
            name = x.name;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new PUser(this, wr);
        }

        /// <summary>
        /// Serialise this Physical to the PhysBase
        /// </summary>
        /// <param name="r">Relocation information for positions</param>
		public override void Serialise(Writer wr)
        {
            wr.PutString(name.ToString());
            base.Serialise(wr);
        }
        /// <summary>
        /// Deserialise this Physical from the buffer
        /// </summary>
        /// <param name="buf">the buffer</param>
        public override void Deserialise(Reader rdr)
        {
            name = rdr.GetString();
            base.Deserialise(rdr);
        }
        /// <summary>
        /// A readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
        public override string ToString()
        {
            return "PUser " + name;
        }
        internal override void Install(Context cx, long p)
        {
            var ro = cx.db.role;
            var nu = new User(this, cx.db);
            // If this is the first User to be defined, 
            // it becomes the Owner of the database
            var first = true;
            for (var b = cx.db.roles.First(); first && b != null; b = b.Next())
                if ((cx.db.objects[b.value()] is User))
                    first = false;
            ro += new ObInfo(nu.defpos, nu.name, Domain.Null);
            cx.db = cx.db + (nu,cx.db.schemaKey) + (Database.Roles,cx.db.roles+(name,ppos))+(ro,p);
            if (first)
                cx.db += (Database.Owner, nu.defpos);
            cx.db += (Database.Log, cx.db.log + (ppos, type));
        }
    }
    internal class Clearance : Physical
    {
        public long _user;
        public Level clearance = Level.D;
        public override long Dependent(Writer wr, Transaction tr)
        {
            if (!Committed(wr,_user)) return _user;
            return -1;
        }
        public Clearance(Reader rdr) : base(Type.Clearance, rdr)
        { }
        public Clearance(long us, Level cl, long pp, Context cx)
            : base(Type.Clearance, pp, cx)
        {
            _user = us;
            clearance = cl;
        }
        protected Clearance(Clearance x, Writer wr) : base(x, wr)
        {
            _user = wr.Fix(x._user);
            clearance = x.clearance;
        }
        protected override Physical Relocate(Writer wr)
        {
            return new Clearance(this, wr);
        }

        public override void Serialise(Writer wr)
        {
            Level.SerialiseLevel(wr, clearance);
            wr.PutLong(_user);
            base.Serialise(wr);
        }
        public override void Deserialise(Reader rdr)
        {
            clearance = Level.DeserialiseLevel(rdr);
            _user = rdr.GetLong();
            base.Deserialise(rdr);
        }
        public override string ToString()
        {
            var sb = new StringBuilder("Clearance " + _user);
            clearance.Append(sb);
            return sb.ToString();
        }

        internal override void Install(Context cx, long p)
        {
            throw new System.NotImplementedException();
        }
    }
}
