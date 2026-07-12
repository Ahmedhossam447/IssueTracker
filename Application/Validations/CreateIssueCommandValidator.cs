using FluentValidation;

namespace IssueTracker.Application.Commands.CreateIssue;

public class CreateIssueCommandValidator : AbstractValidator<CreateIssueCommand>
{
    public CreateIssueCommandValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(200).WithMessage("Title must not exceed 200 characters.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.");
            
        RuleFor(x => x.Priority)
            .IsInEnum().WithMessage("Invalid priority.");
    }
}
