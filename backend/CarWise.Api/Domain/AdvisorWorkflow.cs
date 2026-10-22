namespace CarWise.Api.Domain;

public sealed record AdvisorPendingAction(string Kind,string? Target=null,string? Field=null,string? Subject=null);
public sealed record AdvisorFrame(string? Category,string? Target,string[] Offered,string[] Rejected,string[] Preferences);
public sealed record AdvisorTransition(long Sequence,string Action,string? Target=null,string? Field=null);
public sealed record AdvisorMemory(long Revision=0,string Goal="none",string Phase="idle",AdvisorPendingAction? Pending=null,
    string[]? Preferences=null,AdvisorFrame[]? Backstack=null,AdvisorTransition[]? Transitions=null);
public enum AdvisorDirective { Answer, Continue, Browse, Compare, Clarify, Paused }

/// <summary>One transition owner for the AI tools and deterministic adapters. Financial facts
/// live in the profile/engine, not in conversational memory. Old JSON snapshots migrate on read.</summary>
public sealed class AdvisorWorkflow
{
    public AdvisorState State {get;private set;}
    public AdvisorMemory Memory=>State.Memory!;
    public AdvisorDirective Directive {get;private set;}=AdvisorDirective.Answer;
    public bool NeedsWork=>Directive is AdvisorDirective.Continue or AdvisorDirective.Browse or AdvisorDirective.Compare;
    public AdvisorWorkflow(AdvisorState? state)
    {
        State=state??new();
        State=State with{Memory=State.Memory??new(Goal:State.Target is not null?"compare":State.RequestedCategory is not null?"browse":"none",
            Pending:State.OfferedReferral is {} field?new("representative",Field:field):State.AwaitingField=="alternativeCatalogue"?new("representative",Subject:"catalogue_alternative"):null)};
    }
    private void Change(string action,AdvisorState state,AdvisorMemory? memory=null,string? target=null,string? field=null)
    {
        var previous=Memory;var next=memory??previous;var revision=previous.Revision+1;
        State=state with{Memory=next with{Revision=revision,Transitions=(previous.Transitions??[]).Append(new(revision,action,target,field)).TakeLast(24).ToArray()}};
    }
    private AdvisorMemory Remember()=>Memory with{Backstack=(Memory.Backstack??[]).Append(new(State.RequestedCategory,State.Target,
        State.LastOfferedIds??State.CandidateIds??[],State.RejectedCandidateIds??[],Memory.Preferences??[])).TakeLast(8).ToArray()};
    public void Begin(string category)
    {
        if(Directive==AdvisorDirective.Browse&&State.RequestedCategory==category)return;
        if(State.RequestedCategory==category&&Memory.Transitions?.LastOrDefault()?.Action=="begin"){Directive=AdvisorDirective.Continue;return;}
        var memory=category!=State.RequestedCategory||State.Target is not null?Remember():Memory;
        Change("begin",State with{RequestedCategory=category,Target=category=="waiver"?"waiver":null,WantsElectric=category=="electric",CandidateIds=[],RejectedCandidateIds=[],OfferedReferral=null},memory with{Goal="compare",Pending=null});
        Directive=AdvisorDirective.Continue;
    }
    public void Alternatives(string category)
    {
        if(State.RequestedCategory==category&&State.Target is null&&State.CandidateIds is not {Length:>0}&&Memory.Transitions?.LastOrDefault()?.Action=="request_alternatives")
        {Directive=AdvisorDirective.Browse;return;}
        var rejected=AdvisorDialogue.RejectedChoices(State,category);
        Change("request_alternatives",State with{RequestedCategory=category,Target=null,WantsElectric=category=="electric",CandidateIds=[],RejectedCandidateIds=rejected.ToArray(),OfferedReferral=null},Remember() with{Goal="browse",Pending=null});
        Directive=AdvisorDirective.Browse;
    }
    public void Select(string target)
    {
        var memory=target!=State.Target?Remember():Memory;
        Change("select",State with{Target=target,CandidateIds=[],AwaitingField=null,OfferedReferral=null},memory with{Goal="compare",Pending=null},target);
        Directive=AdvisorDirective.Compare;
    }
    public void Offered(string[] ids)
    {
        if(State.CandidateIds?.SequenceEqual(ids)==true&&Memory.Transitions?.LastOrDefault()?.Action=="offer_vehicles")return;
        Change("offer_vehicles",State with{CandidateIds=ids,LastOfferedIds=ids.Length>0?ids:State.LastOfferedIds,AwaitingField=ids.Length==0?"alternativeCatalogue":null},
            Memory with{Goal=Memory.Goal is "none" or "paused"?"browse":Memory.Goal,Phase=ids.Length==0?"catalogue_exhausted":"choosing",Pending=ids.Length==0?new("representative",Subject:"catalogue_alternative"):null});
    }
    public void Ask(string field)
    {
        Change("ask_field",State with{AwaitingField=field,CandidateIds=[]},Memory with{Phase="collecting",Pending=null},field:field);
        Directive=AdvisorDirective.Continue;
    }
    public void Wait(string field,string? target=null)
    {
        Change("wait_representative",State with{AwaitingField=field,CandidateIds=[],OfferedReferral=null},Memory with{Phase="waiting_representative",Pending=null},target,field);
        Directive=AdvisorDirective.Answer;
    }
    public void Calculated(string target)
    {
        Change("calculated",State with{Target=target,AwaitingField=null,CandidateIds=[]},Memory with{Phase="compared",Pending=null},target);
        Directive=AdvisorDirective.Answer;
    }
    public void OfferAction(AdvisorPendingAction action)
    {
        Change("offer_action",State with{OfferedReferral=action.Kind=="representative"?action.Field:null},Memory with{Pending=action,Phase="confirming"},action.Target,action.Field);
        Directive=AdvisorDirective.Answer;
    }
    public AdvisorPendingAction? Accept()
    {
        var pending=Memory.Pending;
        if(pending is null)return null;
        Change("accept_action",State with{OfferedReferral=null},Memory with{Pending=null},pending.Target,pending.Field);return pending;
    }
    public void Decline()
    {
        Change("decline_action",State with{OfferedReferral=null},Memory with{Pending=null,Phase="idle"});Directive=AdvisorDirective.Answer;
    }
    public void Pause()
    {
        Change("pause",State with{Target=null,AwaitingField=null,CandidateIds=[],OfferedReferral=null},Remember() with{Goal="paused",Phase="paused",Pending=null});Directive=AdvisorDirective.Paused;
    }
    public bool Restore()
    {
        var stack=Memory.Backstack??[];if(stack.Length==0)return false;var frame=stack[^1];
        Change("restore",State with{Target=frame.Target,RequestedCategory=frame.Category,WantsElectric=frame.Category=="electric",CandidateIds=frame.Offered,LastOfferedIds=frame.Offered,RejectedCandidateIds=frame.Rejected,AwaitingField=null,OfferedReferral=null},
            Memory with{Backstack=stack[..^1],Preferences=frame.Preferences,Goal=frame.Target is not null?"compare":"browse",Pending=null});
        Directive=frame.Target is not null?AdvisorDirective.Compare:AdvisorDirective.Browse;return true;
    }
    public void Resume()
    {
        if(Memory.Goal=="paused"&&Restore())return;
        Directive=State.Target is not null?AdvisorDirective.Compare:State.RequestedCategory is not null?AdvisorDirective.Browse:AdvisorDirective.Clarify;
    }
    public void RequestInputs()=>Directive=AdvisorDirective.Continue;
    public void AnswerQuestion(){Change("answer_question",State);Directive=AdvisorDirective.Answer;}
    public void Outcome(string status)=>Directive=status=="needs_input"?AdvisorDirective.Continue:AdvisorDirective.Answer;
    public void Rebind(AdvisorState? state)=>State=(state??State) with{Memory=state?.Memory??Memory};
    public void Clarify(string question)
    {
        Change("clarify",State with{CandidateIds=[]},Memory with{Phase="clarifying",Pending=new("clarification",Subject:question)});Directive=AdvisorDirective.Clarify;
    }
    public void Preference(string preference)
    {
        Change("preference",State,Memory with{Preferences=(Memory.Preferences??[]).Append(preference).Distinct().ToArray(),Pending=null});
    }
    public void ResolveInputs(Employee employee)
    {
        if(State.AwaitingField is {} answered&&IntakeFields.All.Contains(answered)&&IntakeFields.Known(employee,answered))
            State=State with{AwaitingField=null};
        var pending=Memory.Pending;
        if(pending is {Kind:"representative",Field:{} field}&&IntakeFields.All.Contains(field)&&IntakeFields.Known(employee,field))
            Change("input_resolved",State with{OfferedReferral=null},Memory with{Pending=null});
        else if(State.OfferedReferral is {} legacy&&IntakeFields.All.Contains(legacy)&&IntakeFields.Known(employee,legacy))
            State=State with{OfferedReferral=null};
    }
    public void Applied(string action)=>Change(action,State);
    public Employee Attach(Employee employee)=>employee with{AdvisorState=State};
    public object Snapshot(Employee employee,ReferenceData data)=>new{Memory.Revision,Memory.Goal,Memory.Phase,Memory.Pending,
        category=State.RequestedCategory,target=State.Target,awaitingField=State.AwaitingField,
        missingFields=IntakeFields.Missing(employee),knownFields=employee.KnownFields??IntakeFields.All,
        offered=(State.LastOfferedIds??State.CandidateIds??[]).Select(id=>data.Cars.FirstOrDefault(car=>car.Id==id)).Where(car=>car is not null).Select(car=>new{car!.Id,car.Model,car.Year}),
        rejected=State.RejectedCandidateIds??[],preferences=Memory.Preferences??[],previous=Memory.Backstack??[],recentActions=Memory.Transitions??[]};
    public static AdvisorState Reconcile(Employee employee,AdvisorState state,string[] candidates)
    {
        var workflow=new AdvisorWorkflow(state with{Memory=state.Memory??employee.AdvisorState?.Memory});
        workflow.ResolveInputs(employee);
        if(candidates.Length>0&&state.AwaitingField!="currentCar")workflow.Offered(candidates);
        if(workflow.Memory.Pending is null&&workflow.State.OfferedReferral is {} field)workflow.OfferAction(new("representative",Field:field));
        if(workflow.Memory.Pending is null&&state.AwaitingField=="alternativeCatalogue")workflow.OfferAction(new("representative",Subject:"catalogue_alternative"));
        return workflow.State;
    }
}
