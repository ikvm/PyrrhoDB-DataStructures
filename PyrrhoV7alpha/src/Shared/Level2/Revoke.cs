using System;
using Pyrrho.Level1;
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
	/// A Level 2 Revoke request
	/// </summary>
	internal class Revoke : Grant
	{
        /// <summary>
        /// Constructor: Revoke a privilege on an object for a grantee, from the Parser
        /// </summary>
        /// <param name="pr">The privilege</param>
        /// <param name="ob">The object</param>
        /// <param name="ge">The grantee</param>
        /// <param name="pb">The local database</param>
        public Revoke(Privilege pr, long ob, long ge, long u,Transaction db)
            : this(Type.Revoke, pr, ob, ge, u, db)
		{}
        /// <summary>
        /// Constructor: Revoke a privilege on an object for a grantee, from the Parser
        /// </summary>
        /// <param name="tp">The Revoke type</param>
        /// <param name="pr">The privilege</param>
        /// <param name="ob">The object</param>
        /// <param name="ge">The grantee</param>
        /// <param name="pb">The local database</param>
        protected Revoke(Type tp, Privilege pr, long ob, long ge, long u,Transaction db)
            : base(tp, pr, ob, ge, u, db)
		{}
        /// <summary>
        /// Constructor: Revoke a privilege on an object for a grantee, from the buffer
        /// </summary>
        /// <param name="bp">The buffer</param>
        /// <param name="pos">The defining position</param>
		public Revoke(Reader rdr) : base(Physical.Type.Revoke,rdr)
		{}
        protected Revoke(Revoke x, Writer wr) : base(x, wr) { }
        protected override Physical Relocate(Writer wr)
        {
            return new Revoke(this, wr);
        }
        /// <summary>
        /// a readable version of this Physical
        /// </summary>
        /// <returns>the string representation</returns>
		public override string ToString()
		{
			return "Revoke "+priv.ToString()+" on "+Pos(obj)+" from "+Pos(grantee);
		}
	}
}