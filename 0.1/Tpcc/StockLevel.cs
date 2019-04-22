using System;
using System.Drawing;
using Shareable;
using StrongLink;
using System.Collections;
using System.ComponentModel;
using System.Windows.Forms;

namespace Tpcc
{
	/// <summary>
	/// Summary description for StockLevel.
	/// </summary>
	public class StockLevel : VTerm
	{
		/// <summary>
		/// Required designer variable.
		/// </summary>
		private System.ComponentModel.Container components = null;
		public StrongConnect db;
		public Label status;
		public int wid;
		public int did;
		public int thresh;
        bool DoThresh(ref string mess)
        {
            int nextoid = 0;
            db.BeginTransaction();
            var s = db.ExecuteQuery("select D_NEXT_O_ID from DISTRICT where D_W_ID=" + wid + " and D_ID=" + did);
            nextoid = (int)s[0][0];
            s = db.ExecuteQuery("select count(S_I_ID) from STOCK where S_W_ID=" + wid + " and S_I_ID in (select distinct OL_I_ID from ORDER_LINE where OL_W_ID=" + wid + " and OL_D_ID=" + did + " and OL_O_ID>=" + (nextoid - 20) + ") and S_QUANTITY<" + thresh);
            int n = (int)s[0][0];
            Set(4, n);
            db.Rollback();
            return false;
        }

		public void Single()
		{
			string mess = "";
			thresh = util.random(10,20);
			DoThresh(ref mess);
			status.Text = mess;
		}

		public StockLevel(StrongConnect c, int w)
        {
            db = c;
            wid = w;
            //
            // Required for Windows Form Designer support
            //
            InitializeComponent();
			Put(36,1,"Stock-Level");
			Put(1,2,"Warehouse:        District:");
			AddField(12,2,4);
			AddField(29,2,2);
			Put(1,4,"Stock Level Threshold:");
			AddField(24,4,2,true);
			Put(1,6,"low stock:");
			AddField(12,6,3);
			AddField(18,6,1,true);
		}

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		protected override void Dispose( bool disposing )
		{
			if( disposing )
			{
				if(components != null)
				{
					components.Dispose();
				}
			}
			base.Dispose( disposing );
		}
		public override void Activate()
		{
			PutBlanks();
			Set(0,wid);
			Set(1,did);
			SetCurField(2);
		}
		protected override void EnterField(int fn)
		{
			Field f = (Field)fields[fn];
			string s = f.ToString();
			try
			{
				switch(fn)
				{
					case 2: thresh = int.Parse(s); 
						DoThresh(ref s);
						break;
				}
			} 
			catch(Exception ex)
			{
				s = ex.Message;
                if (s.CompareTo("Transaction conflict with read") == 0)
                    Form1.rconflicts++;
                else
                    Form1.wconflicts++;
            }
			SetCurField(curField);
			status.Text = s;
			Invalidate(true);
		}

		#region Windows Form Designer generated code
		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
			this.components = new System.ComponentModel.Container();
			this.Size = new System.Drawing.Size(300,300);
			this.Text = "StockLevel";
		}
		#endregion
	}
}
