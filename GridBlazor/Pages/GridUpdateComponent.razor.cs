﻿using Agno.BlazorInputFile;
using GridBlazor.Columns;
using GridBlazor.Resources;
using GridShared.Columns;
using GridShared.Utility;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace GridBlazor.Pages
{
    public partial class GridUpdateComponent<T> : ICustomGridComponent<T>
    {
        private int _sequence = 0;
        private bool _shouldRender = false;
        private QueryDictionary<RenderFragment> _renderFragments;
        private IEnumerable<string> _tabGroups;

        public string Error { get; set; } = "";
        public QueryDictionary<string> ColumnErrors { get; set; } = new QueryDictionary<string>();
        public QueryDictionary<VariableReference> Children { get; private set; } = new QueryDictionary<VariableReference>();

        public QueryDictionary<VariableReference> InputFiles { get; private set; } = new QueryDictionary<VariableReference>();
        public QueryDictionary<IFileListEntry[]> Files { get; private set; } = new QueryDictionary<IFileListEntry[]>();

        public EditForm Form { get; private set; }

        [Inject]
        private IJSRuntime jSRuntime { get; set; }

        [CascadingParameter(Name = "GridComponent")]
        protected GridComponent<T> GridComponent { get; set; }

        [Parameter]
        public T Item { get; set; }

        protected override async Task OnParametersSetAsync()
        {
            _renderFragments = new QueryDictionary<RenderFragment>();
            foreach (var column in GridComponent.Grid.Columns)
            {
                // Name must have a non empty value
                if (string.IsNullOrWhiteSpace(column.Name))
                    column.Name = Guid.NewGuid().ToString();

                if (((ICGridColumn)column).SubGrids != null)
                {
                    var values = ((ICGridColumn)column).GetSubGridKeyValues(Item);
                    var grid = await ((ICGridColumn)column).SubGrids(values.Values.ToArray(), true, true, true, true) as ICGrid;
                    grid.Direction = GridComponent.Grid.Direction;
                    grid.FixedValues = values;
                    VariableReference reference = new VariableReference();
                    if(Children.ContainsKey(column.Name))
                        Children[column.Name] = reference;
                    else
                        Children.Add(column.Name, reference);
                    if (_renderFragments.ContainsKey(column.Name))
                        _renderFragments[column.Name] = CreateSubGridComponent(grid, reference);
                    else
                        _renderFragments.Add(column.Name, CreateSubGridComponent(grid, reference));
                }
                else if (column.UpdateComponentType != null)
                {
                    VariableReference reference = new VariableReference();
                    if (Children.ContainsKey(column.Name))
                        Children[column.Name] = reference;
                    else
                        Children.Add(column.Name, reference);
                    if (_renderFragments.ContainsKey(column.Name))
                        _renderFragments[column.Name] = GridCellComponent<T>.CreateComponent(_sequence,
                            GridComponent, column.UpdateComponentType, column, Item, null, true, reference);
                    else
                        _renderFragments.Add(column.Name, GridCellComponent<T>.CreateComponent(_sequence,
                            GridComponent, column.UpdateComponentType, column, Item, null, true, reference));
                }
            }
            _tabGroups = GridComponent.Grid.Columns
                .Where(r => !string.IsNullOrWhiteSpace(r.TabGroup) && _renderFragments.Keys.Any(s => s.Equals(r.Name)))
                .Select(r => r.TabGroup).Distinct();

            _shouldRender = true;
        }

        private RenderFragment CreateSubGridComponent(ICGrid grid, VariableReference reference) => builder =>
        {
            Type gridComponentType = typeof(GridComponent<>).MakeGenericType(grid.Type);
            builder.OpenComponent(++_sequence, gridComponentType);
            builder.AddAttribute(++_sequence, "Grid", grid);
            builder.AddComponentReferenceCapture(++_sequence, r => reference.Variable = r);
            builder.CloseComponent();
        };

        protected override bool ShouldRender()
        {
            return _shouldRender;
        }

        protected override void OnAfterRender(bool firstRender)
        {
            _shouldRender = false;
        }
        
        private void SetValue(object value, IGridColumn column)
        {
            var names = column.FieldName.Split('.');
            PropertyInfo pi;
            object obj = Item;
            for (int i = 0; i < names.Length - 1; i++)
            {
                pi = obj.GetType().GetProperty(names[i]);
                obj = pi.GetValue(obj, null);
            }
            pi = obj.GetType().GetProperty(names[names.Length - 1]);
            pi.SetValue(obj, value, null);
        }

        private void ChangeValue(ChangeEventArgs e, IGridColumn column, string typeAttr = null)
        {
            if (string.IsNullOrWhiteSpace(e.Value.ToString()))
            {
                SetValue(null, column);
            }
            else
            {
                if (typeAttr == "week")
                {
                    var value = DateTimeUtils.FromIso8601WeekDate(e.Value.ToString());
                    SetValue(value, column);
                }
                else if (typeAttr == "month")
                {
                    var value = DateTimeUtils.FromMonthDate(e.Value.ToString());
                    SetValue(value, column);
                }
                else
                {
                    var (type, _) = ((IGridColumn<T>)column).GetTypeAndValue(Item);
                    var typeConverter = TypeDescriptor.GetConverter(type);
                    if (typeConverter != null)
                    {
                        try
                        {
                            var value = typeConverter.ConvertFrom(e.Value.ToString());
                            SetValue(value, column);
                        }
                        catch (Exception)
                        {
                            SetValue(null, column);
                        }
                    }
                }
            }
        }

        private void OnFileChange(IGridColumn column, IFileListEntry[] files)
        {
            if (!column.MultipleInput && files.Length > 1)
                files = new IFileListEntry[] { files[0] };

            if (Files.ContainsKey(column.FieldName))
                Files[column.FieldName] = files;
            else
                Files.Add(column.FieldName, files);

            _shouldRender = true;
            StateHasChanged();
        }

        protected async Task UpdateItem()
        {
            try
            {
                Error = "";
                ColumnErrors = new QueryDictionary<string>();
                _tabGroups = null;
                await GridComponent.UpdateItem(this);
            }
            catch (GridException e)
            {
                await OnParametersSetAsync();
                _shouldRender = true;
                Error = string.IsNullOrWhiteSpace(e.Code) ? e.Message :  e.Code + " - " + e.Message;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                await OnParametersSetAsync();
                _shouldRender = true;
                Error = Strings.UpdateError;
            }
        }

        protected async Task ButtonFileClicked(string fieldName)
        {
            var inputFile = InputFiles.Get(fieldName);
            var type = inputFile.Variable.GetType();
            if (type == typeof(InputFile) 
                && ((InputFile)inputFile.Variable).InputFileElement.Id != null)
            {
                await jSRuntime.InvokeVoidAsync("gridJsFunctions.click", (ElementReference)((InputFile)inputFile.Variable).InputFileElement);
            }
        }

        protected void BackButtonClicked()
        {
            GridComponent.BackButton();
        }

    }
}