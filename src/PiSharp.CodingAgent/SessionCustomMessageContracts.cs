using PiSharp.Contracts;
using PiSharp.Agent;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent;

public enum SessionCustomMessageDelivery { Steer, FollowUp, NextTurn }
public enum SessionCustomMessageDisposition { Committed, Queued, Started }
public sealed record SessionCustomMessageDraft(string CustomType, JsonData? Content, bool Display, JsonData? Details = null);
public sealed record SessionCustomMessageReceipt(SessionCustomMessageDisposition Disposition, SessionEntry? Entry = null, AgentLoopResult? Run = null);
