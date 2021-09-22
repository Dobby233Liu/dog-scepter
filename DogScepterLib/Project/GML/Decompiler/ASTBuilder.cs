﻿using DogScepterLib.Core.Models;
using System.Collections.Generic;

using static DogScepterLib.Core.Models.GMCode.Bytecode;

namespace DogScepterLib.Project.GML.Decompiler
{
    public static class ASTBuilder
    {
        // Returns an AST block node from a decompile context that has structures identified and inserted
        public static ASTBlock FromContext(DecompileContext ctx)
        {
            ASTBlock result = new ASTBlock();
            BuildFromNode(ctx, result, ctx.BaseNode);
            return result;
        }

        public class ASTContext
        {
            public ASTNode Current;
            public Node Node;
            public ASTNode Loop;
            public ASTIfStatement IfStatement;

            public ASTContext(ASTNode current, Node node, ASTNode loop, ASTIfStatement ifStatement)
            {
                Current = current;
                Node = node;
                Loop = loop;
                IfStatement = ifStatement;
            }
        }

        // Simulates the stack and builds AST nodes, adding to the "start" node, and using "baseNode" as the data context
        // Also returns the remaining stack, if wanted
        public static Stack<ASTNode> BuildFromNode(DecompileContext dctx, ASTNode start, Node baseNode)
        {
            Stack<ASTContext> statementStack = new Stack<ASTContext>();
            statementStack.Push(new(start, baseNode, null, null));

            Stack<ASTNode> stack = new Stack<ASTNode>();

            while (statementStack.Count != 0)
            {
                var context = statementStack.Pop();
                switch (context.Node.Kind)
                {
                    case Node.NodeType.Block:
                        {
                            Block block = context.Node as Block;
                            if (block.Branches.Count != 0)
                                statementStack.Push(new(context.Current, block.Branches[0], context.Loop, context.IfStatement));
                            ExecuteBlock(dctx, block, context.Current, stack);
                            switch (block.ControlFlow)
                            {
                                case Block.ControlFlowType.Break:
                                    context.Current.Children.Add(new ASTBreak());

                                    // Remove all non-unreachable branches
                                    for (int i = block.Branches.Count - 1; i >= 0; i--)
                                        if (!block.Branches[i].Unreachable)
                                            block.Branches.RemoveAt(i);
                                    break;
                                case Block.ControlFlowType.Continue:
                                    context.Current.Children.Add(new ASTContinue());
                                    if (context.Loop.Kind == ASTNode.StatementKind.WhileLoop)
                                        (context.Loop as ASTWhileLoop).ContinueUsed = true;

                                    // Remove all non-unreachable branches
                                    for (int i = block.Branches.Count - 1; i >= 0; i--)
                                        if (!block.Branches[i].Unreachable)
                                            block.Branches.RemoveAt(i);
                                    break;
                                case Block.ControlFlowType.LoopCondition:
                                    context.Loop.Children.Add(stack.Pop());
                                    break;
                                case Block.ControlFlowType.SwitchExpression:
                                    context.Current.Children.Insert(0, stack.Pop());
                                    break;
                                case Block.ControlFlowType.SwitchCase:
                                    context.Current.Children.Add(new ASTSwitchCase(stack.Pop()));
                                    break;
                                case Block.ControlFlowType.SwitchDefault:
                                    context.Current.Children.Add(new ASTSwitchDefault());
                                    break;
                                case Block.ControlFlowType.IfCondition:
                                case Block.ControlFlowType.WithExpression:
                                case Block.ControlFlowType.RepeatExpression:
                                    // Nothing special to do here
                                    break;
                                default:
                                    if (context.IfStatement != null && stack.Count == context.IfStatement.StackCount + 1 &&
                                        context.IfStatement.Children.Count >= 3 && context.IfStatement.Children.Count < 5)
                                    {
                                        // This is a ternary; add the expression
                                        context.IfStatement.Children.Add(stack.Pop());
                                        if (context.IfStatement.Children.Count >= 5)
                                        {
                                            var removeFrom = context.IfStatement.Parent.Children;
                                            removeFrom.RemoveAt(removeFrom.Count - 1);
                                            stack.Push(context.IfStatement);
                                        }
                                    }
                                    break;
                            }
                        }
                        break;
                    case Node.NodeType.IfStatement:
                        {
                            IfStatement s = context.Node as IfStatement;
                            ExecuteBlock(dctx, s.Header, context.Current, stack);

                            statementStack.Push(new(context.Current, s.Branches[0], context.Loop, context.IfStatement));

                            var astStatement = new ASTIfStatement(stack.Pop());
                            astStatement.StackCount = stack.Count;
                            astStatement.Parent = context.Current;
                            context.Current.Children.Add(astStatement);
                            if (s.Branches.Count >= 3)
                            {
                                // Else block
                                var elseBlock = new ASTBlock();
                                statementStack.Push(new(elseBlock, s.Branches[2], context.Loop, astStatement));

                                // Main/true block
                                var block = new ASTBlock();
                                statementStack.Push(new(block, s.Branches[1], context.Loop, astStatement));

                                astStatement.Children.Add(block);
                                astStatement.Children.Add(elseBlock);
                            }
                            else
                            {
                                // Main/true block
                                var block = new ASTBlock();
                                statementStack.Push(new(block, s.Branches[1], context.Loop, astStatement));
                                astStatement.Children.Add(block);
                            }
                        }
                        break;
                    case Node.NodeType.ShortCircuit:
                        {
                            ShortCircuit s = context.Node as ShortCircuit;

                            if (s.Branches.Count != 0)
                                statementStack.Push(new(context.Current, s.Branches[0], context.Loop, context.IfStatement));

                            var astStatement = new ASTShortCircuit(s.ShortCircuitKind, new List<ASTNode>(s.Conditions.Count));
                            foreach (var cond in s.Conditions)
                            {
                                Stack<ASTNode> returnedStack = BuildFromNode(dctx, context.Current, cond);
                                astStatement.Children.Add(returnedStack.Pop());

                                // The stack remains need to be moved to the main stack
                                ASTNode[] remaining = returnedStack.ToArray();
                                for (int i = remaining.Length - 1; i >= 0; i--)
                                    stack.Push(remaining[i]);

                            }
                            stack.Push(astStatement);
                        }
                        break;
                    case Node.NodeType.Loop:
                        {
                            Loop s = context.Node as Loop;

                            ASTNode astStatement = null;
                            switch (s.LoopKind)
                            {
                                case Loop.LoopType.While:
                                    astStatement = new ASTWhileLoop();
                                    break;
                                case Loop.LoopType.For:
                                    astStatement = new ASTForLoop();
                                    statementStack.Push(new(context.Current, s.Branches[0], astStatement, context.IfStatement));

                                    ASTBlock subBlock2 = new ASTBlock();
                                    astStatement.Children.Add(subBlock2);
                                    statementStack.Push(new(subBlock2, s.Branches[2], context.Loop, context.IfStatement));
                                    break;
                                case Loop.LoopType.DoUntil:
                                    astStatement = new ASTDoUntilLoop();
                                    break;
                                case Loop.LoopType.Repeat:
                                    astStatement = new ASTRepeatLoop(stack.Pop());
                                    break;
                                case Loop.LoopType.With:
                                    astStatement = new ASTWithLoop(stack.Pop());
                                    break;
                            }

                            ASTBlock subBlock = new ASTBlock();
                            astStatement.Children.Add(subBlock);
                            context.Current.Children.Add(astStatement);

                            if (s.LoopKind != Loop.LoopType.For)
                                statementStack.Push(new(context.Current, s.Branches[0], astStatement, context.IfStatement));
                            statementStack.Push(new(subBlock, s.Branches[1], astStatement, context.IfStatement));
                        }
                        break;
                    case Node.NodeType.SwitchStatement:
                        {
                            SwitchStatement s = context.Node as SwitchStatement;

                            if (s.Branches.Count != 0)
                                statementStack.Push(new(context.Current, s.Branches[0], context.Loop, context.IfStatement));

                            var astStatement = new ASTSwitchStatement();
                            context.Current.Children.Add(astStatement);
                            for (int i = s.Branches.Count - 1; i >= 1; i--)
                                statementStack.Push(new(astStatement, s.Branches[i], context.Loop, context.IfStatement));
                        }
                        break;
                }
            }

            return stack;
        }

        public static void ExecuteBlock(DecompileContext ctx, Block block, ASTNode current, Stack<ASTNode> stack)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                Instruction inst = block.Instructions[i];

                switch (inst.Kind)
                {
                    case Instruction.Opcode.Push:
                    case Instruction.Opcode.PushLoc:
                    case Instruction.Opcode.PushGlb:
                    case Instruction.Opcode.PushBltn:
                        switch (inst.Type1)
                        {
                            case Instruction.DataType.Int32:
                                stack.Push(new ASTInt32((int)inst.Value));
                                break;
                            case Instruction.DataType.String:
                                stack.Push(new ASTString((int)inst.Value));
                                break;
                            case Instruction.DataType.Variable:
                                {
                                    ASTVariable variable = new ASTVariable(inst.Variable.Target, inst.Variable.Type);

                                    if (inst.TypeInst == Instruction.InstanceType.StackTop)
                                        variable.Left = stack.Pop();
                                    else if (inst.Variable.Type == Instruction.VariableType.StackTop)
                                        variable.Left = stack.Pop();
                                    else if (inst.Variable.Type == Instruction.VariableType.Array)
                                    {
                                        variable.Children = ProcessArrayIndex(ctx, stack.Pop());
                                        variable.Left = stack.Pop();
                                    }
                                    else
                                        variable.Left = new ASTTypeInst((int)inst.TypeInst);

                                    stack.Push(variable);
                                }
                                break;
                            case Instruction.DataType.Double:
                                stack.Push(new ASTDouble((double)inst.Value));
                                break;
                            case Instruction.DataType.Int16:
                                if ((short)inst.Value == 1)
                                {
                                    if (i >= 2 && i + 1 < block.Instructions.Count)
                                    {
                                        // Check for postfix
                                        Instruction prev1 = block.Instructions[i - 1];
                                        Instruction prev2 = block.Instructions[i - 2];
                                        Instruction next = block.Instructions[i + 1];
                                        if (
                                            // Check for `dup.v`
                                            (prev1.Kind == Instruction.Opcode.Dup && prev1.Type1 == Instruction.DataType.Variable) ||

                                            // Check for `dup.v`, then `pop.e.v` (TODO: Only works before 2.3)
                                            (prev2.Kind == Instruction.Opcode.Dup && prev2.Type1 == Instruction.DataType.Variable &&
                                             prev1.Kind == Instruction.Opcode.Pop && prev1.Type1 == Instruction.DataType.Int16 && prev1.Type1 == Instruction.DataType.Variable))
                                        {
                                            if (next.Kind == Instruction.Opcode.Add || next.Kind == Instruction.Opcode.Sub)
                                            {
                                                // This is a postfix ++/--
                                                // Remove duplicate from stack
                                                stack.Pop();

                                                // Make the statement
                                                stack.Push(new ASTAssign(next, stack.Pop(), false));

                                                // Continue until the end of this operation
                                                while (i < block.Instructions.Count)
                                                {
                                                    if (block.Instructions[i].Kind == Instruction.Opcode.Pop || 
                                                        (block.Instructions[i].Type1 == Instruction.DataType.Int16 && block.Instructions[i].Type2 == Instruction.DataType.Variable))
                                                        i++;
                                                    else
                                                        break;
                                                }

                                                break;
                                            }
                                        }
                                    }
                                    else if (i + 2 < block.Instructions.Count)
                                    {
                                        Instruction next1 = block.Instructions[i + 1];
                                        Instruction next2 = block.Instructions[i + 1];

                                        // Check for add/sub, then `dup.v`
                                        if ((next1.Kind == Instruction.Opcode.Add || next1.Kind == Instruction.Opcode.Sub) &&
                                            (next2.Kind == Instruction.Opcode.Dup && next2.Type1 == Instruction.DataType.Variable))
                                        {
                                            // This is a prefix ++/--
                                            stack.Push(new ASTAssign(next1, stack.Pop(), true));

                                            // Continue until the end of this operation
                                            while (i < block.Instructions.Count && block.Instructions[i].Kind != Instruction.Opcode.Pop)
                                                i++;

                                            // If the end is a pop.e.v, then deal with it properly
                                            // TODO: deal with this in 2.3
                                            if (block.Instructions[i].Type1 == Instruction.DataType.Int16 && block.Instructions[i].Type2 == Instruction.DataType.Variable)
                                            {
                                                ASTNode e = stack.Pop();
                                                stack.Pop();
                                                stack.Push(e);
                                                i++;
                                            }

                                            break;
                                        }
                                    }
                                }
                                stack.Push(new ASTInt16((short)inst.Value, inst.Kind));
                                break;
                            case Instruction.DataType.Int64:
                                stack.Push(new ASTInt64((long)inst.Value));
                                break;
                            case Instruction.DataType.Boolean:
                                stack.Push(new ASTBoolean((bool)inst.Value));
                                break;
                            case Instruction.DataType.Float:
                                stack.Push(new ASTFloat((float)inst.Value));
                                break;
                        }
                        break;
                    case Instruction.Opcode.PushI:
                        switch (inst.Type1)
                        {
                            case Instruction.DataType.Int16:
                                stack.Push(new ASTInt16((short)inst.Value, inst.Kind));
                                break;
                            case Instruction.DataType.Int32:
                                stack.Push(new ASTInt32((int)inst.Value));
                                break;
                            case Instruction.DataType.Int64:
                                stack.Push(new ASTInt64((long)inst.Value));
                                break;
                        }
                        break;
                    case Instruction.Opcode.Pop:
                        {
                            if (inst.Variable == null)
                            {
                                // pop.e.v 5/6 - Swap instruction
                                ASTNode e1 = stack.Pop();
                                ASTNode e2 = stack.Pop();
                                for (int j = 0; j < inst.SwapExtra - 4; j++)
                                    stack.Pop();
                                stack.Push(e2);
                                stack.Push(e1);
                                break;
                            }

                            ASTVariable variable = new ASTVariable(inst.Variable.Target, inst.Variable.Type);

                            ASTNode value = null;
                            if (inst.Type1 == Instruction.DataType.Int32)
                                value = stack.Pop();
                            if (inst.Variable.Type == Instruction.VariableType.StackTop)
                                variable.Left = stack.Pop();
                            else if (inst.Variable.Type == Instruction.VariableType.Array)
                            {
                                variable.Children = ProcessArrayIndex(ctx, stack.Pop());
                                variable.Left = stack.Pop();
                            }
                            else
                                variable.Left = new ASTTypeInst((int)inst.TypeInst);
                            if (inst.Type1 == Instruction.DataType.Variable)
                                value = stack.Pop();

                            // Check for compound operators
                            if (variable.Left.Duplicated &&
                                (inst.Variable.Type == Instruction.VariableType.StackTop || inst.Variable.Type == Instruction.VariableType.Array))
                            {
                                if (value.Kind == ASTNode.StatementKind.Binary && value.Children[0].Kind == ASTNode.StatementKind.Variable)
                                {
                                    ASTBinary binary = value as ASTBinary;
                                    current.Children.Add(new ASTAssign(variable, binary.Children[1], binary.Instruction));
                                    break;
                                }
                            }

                            current.Children.Add(new ASTAssign(variable, value));
                        }
                        break;
                    case Instruction.Opcode.Add:
                    case Instruction.Opcode.Sub:
                    case Instruction.Opcode.Mul:
                    case Instruction.Opcode.Div:
                    case Instruction.Opcode.And:
                    case Instruction.Opcode.Or:
                    case Instruction.Opcode.Mod:
                    case Instruction.Opcode.Rem:
                    case Instruction.Opcode.Xor:
                    case Instruction.Opcode.Shl:
                    case Instruction.Opcode.Shr:
                    case Instruction.Opcode.Cmp:
                        {
                            ASTNode right = stack.Pop();
                            ASTNode left = stack.Pop();
                            stack.Push(new ASTBinary(inst, left, right));
                        }
                        break;
                    case Instruction.Opcode.Call:
                        {
                            List<ASTNode> args = new List<ASTNode>(inst.ArgumentCount);
                            for (int j = 0; j < inst.ArgumentCount; j++)
                                args.Add(stack.Pop());
                            stack.Push(new ASTFunction(inst.Function.Target, args));
                        }
                        break;
                    case Instruction.Opcode.Neg:
                    case Instruction.Opcode.Not:
                        stack.Push(new ASTUnary(inst, stack.Pop()));
                        break;
                    case Instruction.Opcode.Ret:
                        current.Children.Add(new ASTReturn(stack.Pop()));
                        break;
                    case Instruction.Opcode.Exit:
                        current.Children.Add(new ASTExit());
                        break;
                    case Instruction.Opcode.Popz:
                        if (stack.Count == 0)
                            break; // This occasionally happens in switch statements; this is probably the fastest way to handle it
                        current.Children.Add(stack.Pop());
                        break;
                    case Instruction.Opcode.Dup:
                        if (inst.ComparisonKind != 0)
                        {
                            // This is a special instruction for moving around an instance on the stack in GMS2.3
                            throw new System.Exception("Unimplemented GMS2.3");
                        }

                        // Get the number of times duplications should occur
                        // dup.i 1 is the same as dup.l 0
                        int count = ((inst.Extra + 1) * (inst.Type1 == Instruction.DataType.Int64 ? 2 : 1));

                        List<ASTNode> expr1 = new List<ASTNode>();
                        List<ASTNode> expr2 = new List<ASTNode>();
                        for (int j = 0; j < count; j++)
                        {
                            var item = stack.Pop();
                            item.Duplicated = true;
                            expr1.Add(item);
                            expr2.Add(item);
                        }
                        for (int j = count - 1; j >= 0; j--)
                            stack.Push(expr1[j]);
                        for (int j = count - 1; j >= 0; j--)
                            stack.Push(expr2[j]);
                        break;
                    case Instruction.Opcode.Conv:
                        if (inst.Type1 == Instruction.DataType.Int32 && inst.Type2 == Instruction.DataType.Boolean && stack.Peek().Kind == ASTNode.StatementKind.Int16)
                        {
                            // Check if a 0 or 1 should be converted to a boolean for readability, such as in while (true)
                            ASTInt16 val = stack.Peek() as ASTInt16;
                            if (val.Value == 0)
                            {
                                stack.Pop();
                                stack.Push(new ASTBoolean(false));
                            }
                            else if (val.Value == 1)
                            {
                                stack.Pop();
                                stack.Push(new ASTBoolean(true));
                            }
                        }
                        break;
                }
            }
        }

        public static List<ASTNode> ProcessArrayIndex(DecompileContext ctx, ASTNode index)
        {
            // All array indices are normal in 2.3
            if (ctx.Data.VersionInfo.IsNumberAtLeast(2, 3))
                return new() { index };
            
            // Check for 2D array indices
            if (index.Kind == ASTNode.StatementKind.Binary)
            {
                var add = index as ASTBinary;
                if (add.Instruction.Kind == Instruction.Opcode.Add &&
                    add.Children[0].Kind == ASTNode.StatementKind.Binary)
                {
                    var mul = add.Children[0] as ASTBinary;
                    if (mul.Instruction.Kind == Instruction.Opcode.Mul &&
                        mul.Children[1].Kind == ASTNode.StatementKind.Int32 &&
                        (mul.Children[1] as ASTInt32).Value == 32000)
                    {
                        return new() { mul.Children[0], add.Children[1] };
                    }
                }
            }

            return new() { index };
        }
    }
}
