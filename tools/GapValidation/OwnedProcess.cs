using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Start suspended, assign a kill-on-close job, then resume: no child can escape
// between Process.Start and job assignment. Only handles from this invocation are terminated.
public sealed class GapOwnedProcess : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct SA { public int Size; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] struct SI { public int Size; public IntPtr Reserved, Desktop, Title; public int X,Y,XSize,YSize,XCount,YCount,Fill,Flags; public short Show,Reserved2Size; public IntPtr Reserved2,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] struct SIX { public SI Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct PI { public IntPtr Process,Thread; public int ProcessId,ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct BASIC { public long ProcessTime,JobTime; public uint Flags; public UIntPtr Minimum,Maximum; public uint Active; public UIntPtr Affinity; public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IO { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct EXT { public BASIC Basic; public IO Io; public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob; }
    [StructLayout(LayoutKind.Sequential)] struct ACCOUNTING { public long User,Kernel,PeriodUser,PeriodKernel; public uint PageFaults,Total,Active,Terminated; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr security,string name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int type,ref EXT value,uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int type,out ACCOUNTING value,uint size,IntPtr returned);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateFile(string file,uint access,uint sharing,ref SA security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr ps,IntPtr ts,bool inherit,uint flags,IntPtr env,string cwd,ref SIX startup,out PI process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref UIntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,UIntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    IntPtr job,process;
    public int Id { get; private set; }
    static void Check(bool value) { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public GapOwnedProcess(string app,string command,string cwd,string stdout,string stderr)
    {
        IntPtr input=IntPtr.Zero,output=IntPtr.Zero,error=IntPtr.Zero,attributes=IntPtr.Zero,handles=IntPtr.Zero,thread=IntPtr.Zero;
        bool attributesInitialized=false;
        try {
            job=CreateJobObject(IntPtr.Zero,null); Check(job!=IntPtr.Zero);
            var limits=new EXT(); limits.Basic.Flags=0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<EXT>()));
            var sa=new SA { Size=Marshal.SizeOf<SA>(),Inherit=1 };
            input=CreateFile("NUL",0x80000000,3,ref sa,3,0,IntPtr.Zero); Check(input!=new IntPtr(-1));
            output=CreateFile(stdout,0x40000000,3,ref sa,2,0,IntPtr.Zero); Check(output!=new IntPtr(-1));
            error=CreateFile(stderr,0x40000000,3,ref sa,2,0,IntPtr.Zero); Check(error!=new IntPtr(-1));
            UIntPtr size=UIntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
            attributes=Marshal.AllocHGlobal(checked((int)size.ToUInt64()));
            Check(InitializeProcThreadAttributeList(attributes,1,0,ref size)); attributesInitialized=true;
            handles=Marshal.AllocHGlobal(IntPtr.Size*3);
            Marshal.Copy(new[]{input,output,error},0,handles,3);
            Check(UpdateProcThreadAttribute(attributes,0,new IntPtr(0x20002),handles,new UIntPtr((uint)(IntPtr.Size*3)),IntPtr.Zero,IntPtr.Zero));
            var startup=new SIX { Startup=new SI { Size=Marshal.SizeOf<SIX>(),Flags=0x100,Input=input,Output=output,Error=error },Attributes=attributes };
            PI info;
            // Suppress Windows crash dialogs/WER waits in this noninteractive test
            // child. The child inherits this error mode; restore our caller immediately.
            uint oldMode=SetErrorMode(0x0001|0x0002|0x8000);
            try { Check(CreateProcess(app,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,true,0x00080004|0x08000000,IntPtr.Zero,cwd,ref startup,out info)); }
            finally { SetErrorMode(oldMode); }
            process=info.Process; thread=info.Thread; Id=info.ProcessId;
            try { Check(AssignProcessToJobObject(job,process)); }
            catch { TerminateProcess(process,125); throw; }
            Check(ResumeThread(thread)!=uint.MaxValue);
        } catch { Dispose(); throw; }
        finally {
            if(thread!=IntPtr.Zero) CloseHandle(thread);
            foreach(var h in new[]{input,output,error}) if(h!=IntPtr.Zero && h!=new IntPtr(-1)) CloseHandle(h);
            if(attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if(attributes!=IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if(handles!=IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
    }
    public bool Wait(int milliseconds) { uint value=WaitForSingleObject(process,(uint)milliseconds); if(value==0xffffffff) throw new Win32Exception(); return value==0; }
    public int ExitCode { get { uint value; Check(GetExitCodeProcess(process,out value)); return unchecked((int)value); } }
    public void Kill() { if(job!=IntPtr.Zero) Check(TerminateJobObject(job,124)); }
    public void Dispose()
    {
        try {
            if(job!=IntPtr.Zero) {
                Check(TerminateJobObject(job,124));
                var until=DateTime.UtcNow.AddSeconds(10);
                while(true) {
                    ACCOUNTING info;
                    Check(QueryInformationJobObject(job,1,out info,(uint)Marshal.SizeOf<ACCOUNTING>(),IntPtr.Zero));
                    if(info.Active==0) break;
                    if(DateTime.UtcNow>=until) throw new TimeoutException("Owned job still has active processes after termination.");
                    System.Threading.Thread.Sleep(20);
                }
            }
            if(process!=IntPtr.Zero && WaitForSingleObject(process,10000)!=0)
                throw new TimeoutException("Owned direct process did not join after job termination.");
        } finally {
            if(job!=IntPtr.Zero) { CloseHandle(job);job=IntPtr.Zero; }
            if(process!=IntPtr.Zero) { CloseHandle(process);process=IntPtr.Zero; }
        }
    }
}
